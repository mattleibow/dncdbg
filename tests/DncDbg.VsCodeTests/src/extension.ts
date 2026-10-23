import * as path from 'node:path';
import * as vscode from 'vscode';

export interface TestApi {
  readonly messages: readonly unknown[];
  readonly onDidSendMessage: vscode.Event<unknown>;
}

export function activate(context: vscode.ExtensionContext): TestApi {
  const adapterPath = process.env.DNCDBG_DAP_PATH;
  if (!adapterPath) {
    throw new Error('DNCDBG_DAP_PATH must point to the dncdbg-dap apphost.');
  }

  const messages: unknown[] = [];
  const messageEmitter = new vscode.EventEmitter<unknown>();
  const factory: vscode.DebugAdapterDescriptorFactory = {
    createDebugAdapterDescriptor(session) {
      const cwd = session.workspaceFolder?.uri.fsPath ?? path.dirname(adapterPath);
      return new vscode.DebugAdapterExecutable(adapterPath, [], { cwd });
    }
  };
  const trackerFactory: vscode.DebugAdapterTrackerFactory = {
    createDebugAdapterTracker() {
      return {
        onDidSendMessage(message) {
          messages.push(message);
          messageEmitter.fire(message);
        }
      };
    }
  };

  context.subscriptions.push(
    vscode.debug.registerDebugAdapterDescriptorFactory('dncdbg', factory),
    vscode.debug.registerDebugAdapterTrackerFactory('dncdbg', trackerFactory),
    messageEmitter
  );

  return {
    messages,
    onDidSendMessage: messageEmitter.event
  };
}

export function deactivate(): void {
}
