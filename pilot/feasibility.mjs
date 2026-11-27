import { parseArgs } from 'node:util';
import { assessTimeline, localFile, normalizeSrt, parseSrt, readJson, scan, subtitleTypes } from './media.mjs';
import { translate } from './providers.mjs';

const commands = {
  scan: { opts: {}, run: async (input) => { const { srt, ...result } = await scan(input); return result; } },
  timeline: { opts: { anchors: 'string', 'allow-offset': 'boolean' }, run: async (input, opts) => assessTimeline(
    parseSrt(await normalizeSrt(await localFile(input, subtitleTypes))), await readJson(opts.anchors), opts['allow-offset']) },
  translate: { opts: { model: 'string' }, run: translate },
};

async function main() {
  const [command, ...args] = process.argv.slice(2);
  if (!command || command === '--help') {
    console.log('Usage: node pilot/feasibility.mjs <scan|timeline|translate> <file> [options]\nSee pilot/README.md.');
    return;
  }
  if (!Object.hasOwn(commands, command)) throw new Error(`Unknown command: ${command}`);
  const { opts, run } = commands[command];
  const options = Object.fromEntries(Object.entries(opts).map(([name, type]) => [name, { type }]));
  const { positionals, values } = parseArgs({ args, options, allowPositionals: true });
  if (positionals.length !== 1 || (command === 'timeline' && !values.anchors)) throw new Error('Invalid arguments; see pilot/README.md');
  const result = await run(positionals[0], values);
  console.log(JSON.stringify(result, null, 2));
  if (result.passed === false || result.status === 'incomplete') process.exitCode = 2;
}

main().catch((error) => { console.error(error.message); process.exitCode = 1; });
