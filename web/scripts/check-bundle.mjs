// Fails when the production bundle contains anything that must never ship: the demo credentials, or source maps.
// Run after `npm run build`.
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

const dir = join(import.meta.dirname, '..', 'dist', 'support-desk-web', 'browser');
const forbidden = [/DemoPass/, /@acme\.test/, /@globex\.test/];
const problems = [];

for (const file of readdirSync(dir, { recursive: true })) {
  const name = String(file);
  if (name.endsWith('.map')) {
    problems.push(`${name}: source map shipped`);
  }
  if (/\.(js|css|html)$/.test(name)) {
    const text = readFileSync(join(dir, name), 'utf8');
    for (const pattern of forbidden) {
      if (pattern.test(text)) problems.push(`${name}: matches ${pattern}`);
    }
  }
}

if (problems.length) {
  console.error('Production bundle check FAILED:\n  ' + problems.join('\n  '));
  process.exit(1);
}
console.log(`Production bundle check passed (${readdirSync(dir).length} files scanned).`);
