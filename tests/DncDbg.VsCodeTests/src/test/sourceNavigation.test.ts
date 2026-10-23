import * as assert from 'node:assert/strict';
import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import * as vscode from 'vscode';
import type { TestApi } from '../extension';

suite('DNCDbg VS Code integration', () => {
  teardown(async () => {
    vscode.debug.removeBreakpoints(vscode.debug.breakpoints);
    if (vscode.debug.activeDebugSession) {
      await vscode.debug.stopDebugging(vscode.debug.activeDebugSession);
    }
  });

  test('stops in and reuses the workspace source file', async () => {
    const workspace = vscode.workspace.workspaceFolders?.[0];
    assert.ok(workspace, 'The fixture workspace was not opened.');

    const extension = vscode.extensions.getExtension<TestApi>('dncdbg.dncdbg-vscode-tests');
    assert.ok(extension, 'The DNCDbg integration-test extension was not loaded.');
    const api = await extension.activate();

    const sourceUri = vscode.Uri.joinPath(workspace.uri, 'Program.cs');
    const sourceDocument = await vscode.workspace.openTextDocument(sourceUri);
    await vscode.window.showTextDocument(sourceDocument);
    const lines = sourceDocument.getText().split(/\r?\n/);
    const breakpointLine = lines.findIndex(line => line.includes('// BREAKPOINT'));
    assert.notEqual(breakpointLine, -1, 'The fixture breakpoint marker is missing.');

    vscode.debug.addBreakpoints([
      new vscode.SourceBreakpoint(
        new vscode.Location(sourceUri, new vscode.Position(breakpointLine, 0))
      )
    ]);

    const stopped = waitForMessage(api, message =>
      isProtocolMessage(message, 'event', 'stopped'));
    const stackTrace = waitForMessage(api, message =>
      isProtocolMessage(message, 'response', 'stackTrace'));
    const terminated = waitForDebugTermination();

    const started = await vscode.debug.startDebugging(workspace, {
      type: 'dncdbg',
      name: 'DNCDbg source navigation test',
      request: 'launch',
      program: vscode.Uri.joinPath(
        workspace.uri,
        'bin',
        'Debug',
        'net10.0',
        'ConsoleApp.dll'
      ).fsPath,
      cwd: workspace.uri.fsPath,
      stopAtEntry: false,
      justMyCode: true
    });
    assert.equal(started, true, 'VS Code did not start the debug session.');

    const stoppedMessage = await stopped;
    const threadId = protocolBody(stoppedMessage).threadId as number;
    const stackTraceMessage = await stackTrace;
    const source = protocolBody(stackTraceMessage).stackFrames[0].source as {
      path?: string;
      sourceReference?: number;
    };

    assert.equal(source.sourceReference ?? 0, 0,
      'A local workspace file must not be advertised as a virtual DAP source.');
    assert.equal(
      await fs.realpath(source.path ?? ''),
      await fs.realpath(sourceUri.fsPath),
      'The top frame must point at the workspace source file.'
    );

    await waitUntil(() =>
      vscode.window.activeTextEditor?.document.uri.fsPath === sourceUri.fsPath);
    const activeUri = vscode.window.activeTextEditor?.document.uri;
    assert.ok(activeUri, 'VS Code has no active source editor after stopping.');
    assert.equal(activeUri.scheme, 'file', 'VS Code opened a virtual debug source.');
    assert.equal(await fs.realpath(activeUri.fsPath), await fs.realpath(sourceUri.fsPath));

    const programDocuments = vscode.workspace.textDocuments.filter(document =>
      path.basename(document.uri.path) === 'Program.cs');
    assert.equal(
      programDocuments.some(document => document.uri.scheme !== 'file'),
      false,
      `VS Code opened a duplicate virtual Program.cs: ${programDocuments.map(d => d.uri.toString()).join(', ')}`
    );

    await vscode.debug.activeDebugSession?.customRequest('continue', { threadId });
    await terminated;
  });
});

function waitForMessage(
  api: TestApi,
  predicate: (message: unknown) => boolean,
  timeoutMs = 30000
): Promise<unknown> {
  const existing = api.messages.find(predicate);
  if (existing) {
    return Promise.resolve(existing);
  }

  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      subscription.dispose();
      reject(new Error('Timed out waiting for a debug adapter message.'));
    }, timeoutMs);
    const subscription = api.onDidSendMessage(message => {
      if (!predicate(message)) {
        return;
      }
      clearTimeout(timer);
      subscription.dispose();
      resolve(message);
    });
  });
}

function isProtocolMessage(
  message: unknown,
  type: 'event' | 'response',
  name: string
): boolean {
  if (!message || typeof message !== 'object') {
    return false;
  }
  const value = message as Record<string, unknown>;
  return value.type === type && (type === 'event' ? value.event : value.command) === name;
}

function protocolBody(message: unknown): Record<string, any> {
  return (message as { body: Record<string, any> }).body;
}

function waitForDebugTermination(timeoutMs = 30000): Promise<void> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      subscription.dispose();
      reject(new Error('Timed out waiting for the debug session to terminate.'));
    }, timeoutMs);
    const subscription = vscode.debug.onDidTerminateDebugSession(() => {
      clearTimeout(timer);
      subscription.dispose();
      resolve();
    });
  });
}

async function waitUntil(predicate: () => boolean, timeoutMs = 10000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (!predicate()) {
    if (Date.now() >= deadline) {
      throw new Error('Timed out waiting for VS Code to reveal the source file.');
    }
    await new Promise(resolve => setTimeout(resolve, 50));
  }
}
