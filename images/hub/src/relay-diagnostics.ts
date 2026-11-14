const stages = ['STARTUP', 'DISCONNECT', 'DATA_PATH', 'UNCAUGHT', 'UNHANDLED'] as const;
export type RelayDiagnosticStage = typeof stages[number];
const systemCodes = new Set(['ECONNRESET','ECONNREFUSED','ETIMEDOUT','ENOTFOUND','EAI_AGAIN','EHOSTUNREACH','ENETUNREACH','EADDRINUSE','CERT_HAS_EXPIRED','UNABLE_TO_VERIFY_LEAF_SIGNATURE','ERR_TLS_CERT_ALTNAME_INVALID']);

export function relayDiagnosticCode(error: unknown, stage: RelayDiagnosticStage, reason?: number): string {
  const seen = new Set<unknown>();
  let current = error;
  for (let depth = 0; current && typeof current === 'object' && depth < 8 && !seen.has(current); depth++) {
    seen.add(current);
    const e = current as { message?: unknown; code?: unknown; statusCode?: unknown; status?: unknown; cause?: unknown; innerError?: unknown; response?: { status?: unknown } };
    const numeric = e.statusCode ?? e.status ?? e.response?.status;
    if (typeof numeric === 'number' && Number.isInteger(numeric) && numeric >= 100 && numeric <= 599)
      return `RELAY_${stage}_HTTP_${numeric}`;
    if (typeof e.code === 'string' && systemCodes.has(e.code)) return `RELAY_${stage}_${e.code}`;
    const message = typeof e.message === 'string' ? e.message : '';
    const http = /(?:unexpected server response|HTTP status(?: code)?|status code)[^0-9]{0,10}([1-5][0-9]{2})\b/i.exec(message);
    if (http) return `RELAY_${stage}_HTTP_${http[1]}`;
    if (/no hosts are currently accepting connections/i.test(message)) return `RELAY_${stage}_NO_HOST`;
    current = e.cause ?? e.innerError;
  }
  if (Number.isInteger(reason) && reason! >= 0 && reason! <= 255) return `RELAY_${stage}_SSH_${reason}`;
  return `RELAY_${stage}_UNKNOWN`;
}
export function isRelayDiagnosticCode(value: unknown): value is string {
  return typeof value === 'string' && value.length <= 80 &&
    /^RELAY_(?:STARTUP|DISCONNECT|DATA_PATH|UNCAUGHT|UNHANDLED)_(?:HTTP_[1-5][0-9]{2}|SSH_[0-9]{1,3}|NO_HOST|UNKNOWN|ECONNRESET|ECONNREFUSED|ETIMEDOUT|ENOTFOUND|EAI_AGAIN|EHOSTUNREACH|ENETUNREACH|EADDRINUSE|CERT_HAS_EXPIRED|UNABLE_TO_VERIFY_LEAF_SIGNATURE|ERR_TLS_CERT_ALTNAME_INVALID)$/.test(value);
}
export async function reportRelayDiagnostic(error: unknown, stage: RelayDiagnosticStage, reason?: number): Promise<void> {
  if (!process.connected || !process.send) return;
  const code = relayDiagnosticCode(error, stage, reason);
  await new Promise<void>(resolve => {
    const deadline = setTimeout(resolve, 100);
    try { process.send?.({type:'diagnostic',code}, () => { clearTimeout(deadline); resolve(); }); }
    catch { clearTimeout(deadline); resolve(); }
  });
}
