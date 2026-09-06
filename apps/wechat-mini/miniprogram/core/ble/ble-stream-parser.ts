import { BLE, BleFrame, decodeFrame } from '../protocol/ble-codec';
export interface ParserStats { crcErrors:number; resyncBytes:number; sequenceGaps:number; duplicates:number; frames:number }
export class BleStreamParser {
  private buffer = new Uint8Array(0); private lastTelemetrySequence?:number; readonly stats:ParserStats={crcErrors:0,resyncBytes:0,sequenceGaps:0,duplicates:0,frames:0};
  constructor(private readonly maxBuffer=512) {}
  push(input:Uint8Array):BleFrame[]{ if(input.length>0){const merged=new Uint8Array(Math.min(this.maxBuffer,this.buffer.length+input.length)); const start=Math.max(0,this.buffer.length+input.length-this.maxBuffer); const all=new Uint8Array(this.buffer.length+input.length);all.set(this.buffer);all.set(input,this.buffer.length);merged.set(all.subarray(start));this.buffer=merged;} const result:BleFrame[]=[];
    while(this.buffer.length>=2){ let sync=0; while(sync+1<this.buffer.length && !(this.buffer[sync]===BLE.sync0&&this.buffer[sync+1]===BLE.sync1))sync++; if(sync>0){this.stats.resyncBytes+=sync;this.buffer=this.buffer.slice(sync);if(this.buffer.length<2)break;} if(this.buffer.length<12)break; const len=this.buffer[4]|this.buffer[5]<<8; const type=this.buffer[3]; if(this.buffer[2]!==1||![1,2,3,0x80,0x81].includes(type)||len>BLE.maxPayload||(type===1&&len!==42)||(type===2&&len!==59)||(type===3&&len!==8)||(type===0x80&&(len<6||len>128))||(type===0x81&&(len<8||len>96))){this.stats.resyncBytes++;this.buffer=this.buffer.slice(1);continue;} const total=14+len;if(this.buffer.length<total)break; const candidate=this.buffer.slice(0,total); try{const frame=decodeFrame(candidate); const telemetry=frame.type===BLE.types.FAST_WEIGHT||frame.type===BLE.types.SLOW_STATUS||frame.type===BLE.types.CHECKWEIGH_STATUS;if(telemetry){if(this.lastTelemetrySequence!==undefined){const delta=(frame.sequence-this.lastTelemetrySequence+0x10000)&0xffff;if(delta===0)this.stats.duplicates++;else if(delta!==1)this.stats.sequenceGaps++;}this.lastTelemetrySequence=frame.sequence;}this.stats.frames++;result.push(frame);this.buffer=this.buffer.slice(total);}catch{this.stats.crcErrors++;this.buffer=this.buffer.slice(1);} }
    return result;
  }
  get bufferedBytes(){return this.buffer.length}
  resetStatistics(){this.stats.crcErrors=0;this.stats.resyncBytes=0;this.stats.sequenceGaps=0;this.stats.duplicates=0;this.stats.frames=0;}
}
