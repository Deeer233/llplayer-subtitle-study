import { MPEGDecoder } from 'mpg123-decoder';
import wasm from '../.generated/decoder.wasm';

MPEGDecoder.module = wasm;

export async function isShortAudio(bytes) {
  const decoder = new MPEGDecoder();
  await decoder.ready;
  try {
    let total = 0, rate = 0, audible = false;
    for (let offset = 0; offset < bytes.length; offset += 8192) {
      const result = decoder.decode(bytes.subarray(offset, offset + 8192));
      if (result.errors.length) return false;
      if (!result.samplesDecoded) continue;
      if (rate && rate !== result.sampleRate) return false;
      rate = result.sampleRate;
      total += result.samplesDecoded;
      if (!rate || total > 30 * rate) return false;
      if (!audible) {
        const [left,right] = result.channelData;
        for (let i = 0; i < left.length && !audible; i++) {
          audible = Math.abs((left[i] + (right?.[i] || left[i])) / 2) >= 50 / 32768;
        }
      }
    }
    return total > 0 && audible;
  } finally {
    decoder.free();
  }
}
