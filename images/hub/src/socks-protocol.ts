/** Pure, incremental SOCKS5 parsing. No sockets, resolution or target access. */
export class SocksProtocolError extends Error {
  constructor(
    readonly replyCode: number,
    readonly method = false,
  ) {
    super("Invalid SOCKS request");
  }
}

interface Address {
  host: string;
  end: number;
}
export interface ConnectRequest extends Address {
  port: number;
}

export function greetingLength(input: Buffer): number | undefined {
  if (input.length < 2) {
    return undefined;
  }
  if (input[0] !== 5) {
    throw new SocksProtocolError(1);
  }
  if (!input[1]) {
    throw new SocksProtocolError(1, true);
  }
  const end = 2 + input[1];
  if (input.length < end) {
    return undefined;
  }
  if (!input.subarray(2, end).includes(0)) {
    throw new SocksProtocolError(1, true);
  }
  return end;
}

function domainAddress(input: Buffer): Address | undefined {
  if (input.length < 5) {
    return undefined;
  }
  const length = input[4];
  if (!length || length > 253) {
    throw new SocksProtocolError(8);
  }
  const end = 7 + length;
  if (input.length < end) {
    return undefined;
  }
  const bytes = input.subarray(5, 5 + length);
  // Require IDNA ASCII and DNS labels to prevent authority/header injection.
  if (!bytes.every((byte) => byte >= 0x21 && byte <= 0x7e)) {
    throw new SocksProtocolError(8);
  }
  const host = bytes.toString("ascii").toLowerCase().replace(/\.$/, "");
  if (
    !host
      ?.split(".")
      .every((label) => /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(label))
  ) {
    throw new SocksProtocolError(8);
  }
  return { host, end };
}

function address(input: Buffer): Address | undefined {
  switch (input[3]) {
    case 1:
      if (input.length < 10) {
        return undefined;
      }
      return { host: [...input.subarray(4, 8)].join("."), end: 10 };
    case 4:
      if (input.length < 22) {
        return undefined;
      }
      return {
        host: `[${Array.from({ length: 8 }, (_, i) => input.readUInt16BE(4 + i * 2).toString(16)).join(":")}]`,
        end: 22,
      };
    case 3:
      return domainAddress(input);
    default:
      throw new SocksProtocolError(8);
  }
}

export function connectRequest(input: Buffer): ConnectRequest | undefined {
  if (input.length < 4) {
    return undefined;
  }
  if (input[0] !== 5 || input[2] !== 0) {
    throw new SocksProtocolError(1);
  }
  if (input[1] !== 1) {
    throw new SocksProtocolError(7);
  }
  const target = address(input);
  if (!target) {
    return undefined;
  }
  const port = input.readUInt16BE(target.end - 2);
  if (!port) {
    throw new SocksProtocolError(1);
  }
  return { ...target, port };
}
