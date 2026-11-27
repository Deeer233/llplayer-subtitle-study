import { mkdirSync, writeFileSync } from 'node:fs';
import { MPEGDecoder } from 'mpg123-decoder';

// This build step captures the exact module compiled by the locked decoder.
// Workers imports that module as a binding instead of compiling bytes at request time.
const chunks = [];
const compile = WebAssembly.compile;
WebAssembly.compile = function (bytes) {
  chunks.push(Buffer.from(bytes));
  return compile.call(WebAssembly, bytes);
};
try {
  const decoder = new MPEGDecoder();
  await decoder.ready;
  decoder.free();
} finally {
  WebAssembly.compile = compile;
}
// The pinned decoder's minified glue binds these four MPEG functions to p/q/r/s.
const matches = chunks.filter(bytes => {
  const exports = WebAssembly.Module.exports(new WebAssembly.Module(bytes));
  return ['p','q','r','s'].every(name=>exports.some(item=>item.name===name && item.kind==='function'));
});
if (matches.length !== 1) throw new Error('Expected exactly one MPEG decoder WASM module.');
mkdirSync(new URL('./.generated/', import.meta.url), {recursive:true});
writeFileSync(new URL('./.generated/decoder.wasm', import.meta.url),matches[0]);
