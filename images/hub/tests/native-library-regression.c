/* Read-only runtime smoke of the installed replacement libraries, not their
 * build-tree copies. Fixtures are local strings; no network or account data.
 */
#include <assert.h>
#include <dlfcn.h>
#include <stdio.h>
#include <string.h>

int main(void) {
  const char *names[] = {"libexpat.so.1", "libexpatw.so.1"};
  for (size_t i = 0; i < 2; i++) {
    void *lib = dlopen(names[i], RTLD_NOW | RTLD_LOCAL); assert(lib);
    const char *(*version)(void) = dlsym(lib, "XML_ExpatVersion");
    void *(*create)(const void *) = dlsym(lib, "XML_ParserCreate");
    int (*parse)(void *, const char *, int, int) = dlsym(lib, "XML_Parse");
    void (*destroy)(void *) = dlsym(lib, "XML_ParserFree");
    assert(version && create && parse && destroy);
    assert(strcmp(version(), "expat_2.8.4") == 0);
    void *parser = create(NULL); assert(parser);
    const char *valid = "<root><item>synthetic</item></root>";
    assert(parse(parser, valid, (int)strlen(valid), 1) == 1); destroy(parser);
    parser = create(NULL); assert(parser);
    const char *invalid = "<root>\xed\xb0\x80</root>";
    assert(parse(parser, invalid, (int)strlen(invalid), 1) == 0); destroy(parser);
    printf("PASS %s: version 2.8.4, XML parsing and invalid surrogate rejection\n", names[i]);
    dlclose(lib);
  }
  void *gio = dlopen("libgio-2.0.so.0", RTLD_NOW | RTLD_LOCAL); assert(gio);
  void *(*node_info)(const char *, void **) = dlsym(gio, "g_dbus_node_info_new_for_xml");
  void (*free_error)(void *) = dlsym(gio, "g_error_free");
  assert(node_info && free_error);
  const char *invalid_nodes[] = {
    "<node><interface name='I'><method name='M'><node/></method></interface></node>",
    "<node><interface name='I'><signal name='S'><node/></signal></interface></node>",
    "<node><interface name='I'><property name='P' type='s' access='read'><node/></property></interface></node>",
    "<node><interface name='I'><method name='M'><arg type='s'><node/></arg></method></interface></node>"
  };
  for (size_t i = 0; i < 4; i++) {
    void *error = NULL;
    assert(node_info(invalid_nodes[i], &error) == NULL);
    assert(error); free_error(error);
  }
  puts("PASS CVE-2026-58016: four invalid node nesting cases rejected");
  dlclose(gio);
}
