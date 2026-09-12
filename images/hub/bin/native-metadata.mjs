// Parse readelf output in linear passes, including malformed or oversized lines.
// SONAMEs are ABI identifiers, never commands or paths to execute.
export function sonameFromDynamic(output) {
  for (const line of output.split("\n")) {
    const marker = line.indexOf("(SONAME)");
    if (marker < 0) continue;
    const start = line.indexOf("[", marker + 8);
    const end = line.indexOf("]", start + 1);
    if (start >= 0 && end > start + 1) return line.slice(start + 1, end);
  }
  return undefined;
}
