import path from 'node:path';
import os from 'node:os';
import { fileURLToPath } from 'node:url';
import { defineConfig } from '@vscode/test-cli';

const extensionRoot = path.dirname(fileURLToPath(import.meta.url));
const tempRoot = process.env.DNCDBG_VSCODE_TEST_TEMP
  ?? path.join(os.tmpdir(), `dncdbg-vscode-${process.pid}`);
process.env.DNCDBG_DAP_PATH ??= path.resolve(extensionRoot, '../../bin/dncdbg-dap');

export default defineConfig({
  files: 'out/test/**/*.test.js',
  version: process.env.DNCDBG_VSCODE_VERSION ?? 'insiders',
  workspaceFolder: './fixture',
  extensionDevelopmentPath: '.',
  launchArgs: [
    `--user-data-dir=${path.join(tempRoot, 'user')}`,
    `--extensions-dir=${path.join(tempRoot, 'extensions')}`,
    '--disable-extensions',
    '--disable-workspace-trust',
    '--skip-welcome',
    '--skip-release-notes'
  ],
  mocha: {
    ui: 'tdd',
    timeout: 60000
  }
});
