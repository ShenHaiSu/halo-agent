import { describe, expect, it } from 'vitest';
import type { ContractEventFrame } from '@/contract/agent';
import { FrameToolFeed, TOOL_MATRIX, TOOL_OUTPUT, describeToolError } from './frameToolFeed';

let seq = 0;
function frame(kind: ContractEventFrame['k'], params: Record<string, unknown>): ContractEventFrame {
  seq += 1;
  return { t: 'evt', seq, k: kind, p: params };
}

function fullCardCycle(toolName: string, callId: string): FrameToolFeed {
  const feed = new FrameToolFeed();
  feed.observe(
    frame('tool.start', { turnId: 'turn-1', toolCallId: callId, toolName, argsPreview: '{"file_path":"a.txt"}', policy: 'write' })
  );
  feed.observe(frame('tool.delta', { turnId: 'turn-1', toolCallId: callId, delta: ',\"limit\":5}' }));
  feed.observe(
    frame('tool.end', {
      turnId: 'turn-1',
      toolCallId: callId,
      ok: true,
      success: true,
      output: 'file content here',
      exitCode: 0,
      durationMs: 12,
      fileBytes: 18,
      images: [{ imageId: 'img-1', fileName: 'a.png', mime: 'image/png', relPath: 'r/a.png', width: 8, height: 6, sizeBytes: 10 }],
      validatedWarnings: ['VALIDATED_DEFAULTED:limit'],
    })
  );
  return feed;
}

describe('frame tool feed card matrix', () => {
  it('covers all fifteen catalog tools with full fields', () => {
    expect(TOOL_MATRIX).toHaveLength(15);
    for (const toolName of TOOL_MATRIX) {
      const feed = fullCardCycle(toolName, `call-${toolName}`);
      const card = feed.cardOf(`call-${toolName}`);
      expect(card?.toolName).toBe(toolName);
      expect(card?.status).toBe('done');
      expect(card?.argsText).toContain('a.txt');
      expect(card?.output).toBe('file content here');
      expect(card?.durationMs).toBe(12);
      expect(card?.fullOutput).toBe('file content here');
      expect(card?.images).toHaveLength(1);
      expect(card?.validatedWarnings).toEqual(['VALIDATED_DEFAULTED:limit']);
      const projected = feed.toAgentCard(`call-${toolName}`);
      expect(projected?.functionName).toBe(toolName);
      expect(projected?.status).toBe('done');
      expect(projected?.images).toHaveLength(1);
    }
  });

  it('streams progress through the single delta field with coalescing counts', () => {
    seq = 0;
    const feed = new FrameToolFeed();
    feed.observe(frame('tool.start', { turnId: 'turn-2', toolCallId: 'call-2', toolName: 'exec_command' }));
    feed.observe(frame('tool.delta', { turnId: 'turn-2', toolCallId: 'call-2', delta: '{"co' }));
    feed.observe(frame('tool.delta', { turnId: 'turn-2', toolCallId: 'call-2', delta: 'mmand":"ls"}' }));
    expect(feed.cardOf('call-2')?.argsText).toBe('{"command":"ls"}');
    expect(feed.cardOf('call-2')?.receivedBytes).toBeGreaterThan(0);
    expect(feed.stats().coalescedDeltas).toBe(2);
  });

  it('rejects stale incremental-args keys instead of merging them', () => {
    seq = 0;
    const feed = new FrameToolFeed();
    feed.observe(frame('tool.start', { turnId: 'turn-3', toolCallId: 'call-3', toolName: 'read_file' }));
    const bad: ContractEventFrame = { t: 'evt', seq: 99, k: 'tool.delta', p: { turnId: 'turn-3', toolCallId: 'call-3', argsDelta: 'x' } };
    expect(feed.observe(bad)).toBe(false);
    expect(feed.cardOf('call-3')?.argsText).toBe('');
    expect(feed.stats().rejectedLegacyArgs).toBe(1);
  });

  it('settles failure codes, spill files and long output behind full text', () => {
    seq = 0;
    const feed = new FrameToolFeed();
    feed.observe(frame('tool.start', { turnId: 'turn-4', toolCallId: 'call-4', toolName: 'edit_file' }));
    feed.observe(
      frame('tool.end', {
        turnId: 'turn-4',
        toolCallId: 'call-4',
        ok: false,
        success: false,
        output: 'nope',
        errorCode: 'OLD_STRING_NOT_FOUND',
        errorPayload: '{"anchor":"xyz"}',
        spilledFile: 'spill/turn-4.log',
      })
    );
    const card = feed.cardOf('call-4');
    expect(card?.status).toBe('error');
    expect(card?.errorCode).toBe('OLD_STRING_NOT_FOUND');
    expect(card?.errorPayload).toBe('{"anchor":"xyz"}');
    expect(card?.spilledFile).toBe('spill/turn-4.log');
    expect(describeToolError('OLD_STRING_NOT_FOUND')).toContain('原文');
    expect(describeToolError('[SUBAGENT_INTERNAL]')).toContain('子代理');
    expect(describeToolError('[PERMISSION_DENIED:skill_tool:write]')).toContain('权限');
    expect(describeToolError('COMMAND_TIMEOUT')).toContain('命令');

    const longFeed = new FrameToolFeed();
    longFeed.observe(frame('tool.start', { turnId: 'turn-5', toolCallId: 'call-5', toolName: 'read_file' }));
    const big = 'x'.repeat(TOOL_OUTPUT.display + 500);
    longFeed.observe(frame('tool.end', { turnId: 'turn-5', toolCallId: 'call-5', ok: true, output: big }));
    expect(longFeed.cardOf('call-5')?.outputTruncated).toBe(true);
    expect(longFeed.cardOf('call-5')?.fullOutput).toBe(big);
    expect(longFeed.cardOf('call-5')?.output.startsWith(big.slice(0, TOOL_OUTPUT.display))).toBe(true);
    expect(longFeed.cardOf('call-5')?.output).toContain('截断');
  });

  it('dedups cursor replays per card lane', () => {
    seq = 0;
    const feed = new FrameToolFeed();
    feed.observe(frame('tool.start', { turnId: 'turn-6', toolCallId: 'call-6', toolName: 'read_file' }));
    const duplicate: ContractEventFrame = {
      t: 'evt',
      seq: 1,
      k: 'tool.start',
      p: { turnId: 'turn-6', toolCallId: 'call-6', toolName: 'read_file' },
    };
    expect(feed.observe(duplicate)).toBe(false);
    expect(feed.stats().dedupDrops).toBe(1);
  });
});
