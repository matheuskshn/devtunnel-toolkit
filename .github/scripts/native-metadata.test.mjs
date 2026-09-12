import test from "node:test";
import assert from "node:assert/strict";
import { sonameFromDynamic } from "../../images/hub/bin/native-metadata.mjs";

test("native SONAME parsing preserves ABI names and handles adversarial readelf output", () => {
  assert.equal(
    sonameFromDynamic(" 0x000e (SONAME) Library soname: [libexample.so.1]\n"),
    "libexample.so.1",
  );
  for (const output of [
    "",
    "(NEEDED) [libexample.so.1]",
    "(SONAME) []",
    "(SONAME) [",
    "[bad] (SONAME)",
  ])
    assert.equal(sonameFromDynamic(output), undefined);
  assert.equal(
    sonameFromDynamic("(SONAME) " + "[".repeat(1024 * 1024)),
    undefined,
  );
  assert.equal(
    sonameFromDynamic("x".repeat(1024 * 1024) + "\n(SONAME) [libexample.so.2]"),
    "libexample.so.2",
  );
});
