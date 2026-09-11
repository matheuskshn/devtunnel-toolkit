/* Synthetic local check for the missing-data elf_strptr case, CVE-2025-1376.
 * Uses only a newly created anonymous temporary file. Not a general ELF audit.
 */
#include <assert.h>
#include <dlfcn.h>
#include <elf.h>
#include <libelf.h>
#include <stdio.h>
#include <stddef.h>

int main(void) {
  void *lib = dlopen("libelf.so.1", RTLD_NOW | RTLD_LOCAL);
  assert(lib != NULL);
  unsigned int (*version)(unsigned int) = dlsym(lib, "elf_version");
  void *(*begin)(int, int, void *) = dlsym(lib, "elf_begin");
  void *(*newehdr)(void *) = dlsym(lib, "elf64_newehdr");
  void *(*newscn)(void *) = dlsym(lib, "elf_newscn");
  Elf64_Shdr *(*getshdr)(void *) = dlsym(lib, "elf64_getshdr");
  size_t (*ndxscn)(void *) = dlsym(lib, "elf_ndxscn");
  char *(*strptr)(void *, size_t, size_t) = dlsym(lib, "elf_strptr");
  int (*end)(void *) = dlsym(lib, "elf_end");
  assert(version && begin && newehdr && newscn && getshdr && ndxscn && strptr && end);
  assert(version(EV_CURRENT) == EV_CURRENT);
  FILE *file = tmpfile(); assert(file != NULL);
  void *elf = begin(fileno(file), ELF_C_WRITE, NULL); assert(elf != NULL);
  assert(newehdr(elf) != NULL);
  void *section = newscn(elf); assert(section != NULL);
  Elf64_Shdr *header = getshdr(section); assert(header != NULL);
  header->sh_type = SHT_STRTAB;
  header->sh_size = 32;
  assert(strptr(elf, ndxscn(section), 0) == NULL);
  end(elf); fclose(file); dlclose(lib);
  puts("PASS CVE-2025-1376 regression: missing section data rejected without crash");
}
