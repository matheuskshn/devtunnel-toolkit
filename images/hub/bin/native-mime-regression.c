/* Local synthetic regression for CVE-2026-16118. No network or user files.
 * Compile the actual upstream parser with AddressSanitizer, before and after
 * the upstream fix, so a skipped byte-swap path cannot produce a false pass.
 */
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#if __BYTE_ORDER__ != __ORDER_LITTLE_ENDIAN__
#error This regression covers the affected little-endian parser path
#endif
#ifndef LITTLE_ENDIAN
#define LITTLE_ENDIAN 1
#endif
#include "xdgmimemagic.c"

int main(void) {
  for (unsigned int word = 2; word <= 4; word += 2) {
    for (unsigned int mask = 0; mask <= 1; mask++) {
      FILE *stream = tmpfile();
      assert(stream != NULL);
      const unsigned int length = word * 2;
      fputs(">0=", stream);
      fputc(0, stream);
      fputc((int)length, stream);
      for (unsigned int i = 0; i < length; i++) fputc((int)i + 1, stream);
      if (mask) {
        fputc('&', stream);
        for (unsigned int i = 0; i < length; i++) fputc(0xff, stream);
      }
      fprintf(stream, "~%u\n", word);
      rewind(stream);
      XdgMimeMagicMatch match = {0};
      assert(_xdg_mime_magic_parse_magic_line(stream, &match) == XDG_MIME_MAGIC_MAGIC);
      assert(match.matchlet != NULL && match.matchlet->word_size == word);
      assert(match.matchlet->value_length == length);
      for (unsigned int i = 0; i < length; i++) {
        unsigned int expected = (i / word) * word + word - (i % word);
        assert(match.matchlet->value[i] == expected);
        if (mask) assert(match.matchlet->mask[i] == 0xff);
      }
      _xdg_mime_magic_matchlet_free(match.matchlet);
      fclose(stream);
    }
  }
  puts("PASS CVE-2026-16118: 16/32-bit byte swaps, values and masks, ASan");
  return 0;
}
