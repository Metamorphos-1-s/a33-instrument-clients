import test from 'node:test';
import assert from 'node:assert/strict';
import { BleStreamParser } from '../miniprogram/core/ble/ble-stream-parser';
import { encodeCommandResponse, encodeFrame } from '../miniprogram/core/protocol/ble-codec';

const payload = (type:number) => new Uint8Array(type===1?42:type===2?59:type===3?8:8);
const telemetry = (sequence:number,type=1) => encodeFrame(type,sequence,0,payload(type));
const response = (sequence:number,transactionId=1,operation=1) => encodeCommandResponse({transactionId,operation,result:0,detailCode:0,data:new Uint8Array(0)},sequence,0);
const push = (parser:BleStreamParser,...frames:Uint8Array[]) => parser.push(Uint8Array.from(frames.flatMap(frame=>[...frame])));

test('continuous telemetry shares one sequence domain across all telemetry types',()=>{const p=new BleStreamParser();push(p,telemetry(100,1),telemetry(101,2),telemetry(102,3));assert.equal(p.stats.sequenceGaps,0);assert.equal(p.stats.duplicates,0)});
test('missing telemetry increments one gap event',()=>{const p=new BleStreamParser();push(p,telemetry(100),telemetry(102));assert.equal(p.stats.sequenceGaps,1)});
test('duplicate telemetry increments duplicates',()=>{const p=new BleStreamParser();push(p,telemetry(100),telemetry(100));assert.equal(p.stats.duplicates,1)});
test('telemetry sequence wraps without a gap',()=>{const p=new BleStreamParser();push(p,telemetry(65535),telemetry(0));assert.equal(p.stats.sequenceGaps,0)});
test('two command responses inserted between telemetry do not affect telemetry diagnostics',()=>{const p=new BleStreamParser();push(p,telemetry(1500),response(0),response(1),telemetry(1501));assert.equal(p.stats.sequenceGaps,0);assert.equal(p.stats.duplicates,0);assert.equal(p.stats.frames,4)});
test('interleaved command responses do not affect telemetry diagnostics',()=>{const p=new BleStreamParser();push(p,telemetry(1500),response(0),telemetry(1501),response(1),telemetry(1502));assert.equal(p.stats.sequenceGaps,0);assert.equal(p.stats.duplicates,0)});
test('duplicate and reverse command response sequences do not affect telemetry diagnostics',()=>{const p=new BleStreamParser();push(p,telemetry(9),response(7),response(7),response(3),telemetry(10));assert.equal(p.stats.sequenceGaps,0);assert.equal(p.stats.duplicates,0)});
test('command request frame sequence does not change telemetry baseline',()=>{const p=new BleStreamParser();const request=encodeFrame(0x80,600,0,new Uint8Array(6));push(p,telemetry(20),request,telemetry(21));assert.equal(p.stats.sequenceGaps,0);assert.equal(p.stats.duplicates,0)});
test('resetStatistics retains telemetry baseline and detects a subsequent real loss',()=>{const p=new BleStreamParser();push(p,telemetry(100));p.resetStatistics();push(p,telemetry(102));assert.equal(p.stats.sequenceGaps,1);assert.equal(p.stats.frames,1)});
test('resetStatistics preserves a buffered partial frame',()=>{const p=new BleStreamParser();const frame=telemetry(100);p.push(frame.slice(0,7));p.resetStatistics();assert.equal(p.push(frame.slice(7)).length,1);assert.equal(p.stats.resyncBytes,0);assert.equal(p.stats.frames,1)});
test('bounded buffer retains and decodes the newest complete frame',()=>{const p=new BleStreamParser(512);const frame=telemetry(100);const input=new Uint8Array(600+frame.length);input.fill(0x33,0,600);input.set(frame,600);assert.equal(p.push(input).length,1);assert.ok(p.bufferedBytes<=512);assert.equal(p.stats.frames,1)});
