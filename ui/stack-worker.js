/* Isolated smoke test for Web Worker support. No Git.mil or ARC access. */
self.onmessage=function(event){const n=Number(event.data?.value||0);self.postMessage({ok:n===21,answer:n*2})};
