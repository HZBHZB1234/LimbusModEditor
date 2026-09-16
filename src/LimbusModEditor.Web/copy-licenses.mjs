// Copy Spine runtime licenses to dist/licenses/
// t23 compliance: Spine Runtimes License must be included in distribution
import { copyFileSync, mkdirSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const licensesDir = join(__dirname, 'dist', 'licenses');

// Ensure licenses directory exists
mkdirSync(licensesDir, { recursive: true });

// Copy spine-webgl license
const spineWebglLicense = join(__dirname, 'node_modules', '@esotericsoftware', 'spine-webgl', 'LICENSE');
if (existsSync(spineWebglLicense)) {
    copyFileSync(spineWebglLicense, join(licensesDir, 'spine-webgl-LICENSE'));
    console.log('Copied: spine-webgl-LICENSE');
} else {
    console.warn('WARNING: spine-webgl LICENSE not found at', spineWebglLicense);
}

// Copy spine-core license (dependency of spine-webgl)
const spineCoreLicense = join(__dirname, 'node_modules', '@esotericsoftware', 'spine-core', 'LICENSE');
if (existsSync(spineCoreLicense)) {
    copyFileSync(spineCoreLicense, join(licensesDir, 'spine-core-LICENSE'));
    console.log('Copied: spine-core-LICENSE');
} else {
    console.warn('WARNING: spine-core LICENSE not found at', spineCoreLicense);
}

console.log('License copy complete.');
