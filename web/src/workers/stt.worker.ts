// Speech-to-text worker: Whisper tiny.en through transformers.js, entirely in this browser tab (the TalkForge files, as
// Lazarette uses them). The model and runtime come from the engine's /stt/ paths, read-only from TalkForge's assets.
// Remote model hosts are switched off, so a missing file is an error, never a download. Audio arrives as 16 kHz mono
// Float32 samples and never leaves the tab.
/// <reference lib="webworker" />

interface Asr { (audio: Float32Array): Promise<{ text?: string } | Array<{ text?: string }>> }
interface TransformersLib {
  env: { allowRemoteModels: boolean; allowLocalModels: boolean; localModelPath: string; useBrowserCache: boolean; backends: { onnx: { wasm: { wasmPaths: string; numThreads: number } } } };
  pipeline(task: string, model: string, opts: Record<string, unknown>): Promise<Asr>;
}

let asr: Asr | null = null;
let loading: Promise<void> | null = null;

async function load() {
  const url = '/stt/transformers.min.js';   // served by the engine at run time, never bundled
  const lib = (await import(url)) as TransformersLib;
  lib.env.allowRemoteModels = false;
  lib.env.allowLocalModels = true;
  lib.env.localModelPath = '/stt/models/';
  lib.env.useBrowserCache = false;
  lib.env.backends.onnx.wasm.wasmPaths = '/stt/onnx/';
  lib.env.backends.onnx.wasm.numThreads = 1;
  asr = await lib.pipeline('automatic-speech-recognition', 'whisper-tiny.en', { dtype: 'q8', device: 'wasm' });
}

self.onmessage = async (e: MessageEvent<{ type: string; id?: number; audio?: Float32Array }>) => {
  const m = e.data;
  try {
    loading ??= load();
    await loading;
    if (m.type === 'load') { self.postMessage({ type: 'ready', id: m.id }); return; }
    const out = await asr!(m.audio!);
    const text = Array.isArray(out) ? out.map((o) => o.text ?? '').join(' ') : (out.text ?? '');
    self.postMessage({ type: 'text', id: m.id, text: text.trim() });
  } catch (err) {
    loading = null;
    self.postMessage({ type: 'error', id: m.id, message: err instanceof Error ? err.message : String(err) });
  }
};
