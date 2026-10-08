/* QA-only native Vulkan bootstrap probe. No logical device, surface, or game.
 * ABI declarations match the Vulkan 1.0 instance APIs in vulkan_core.h.
 * Run only with a library from the separately owned, hash-pinned Wine archive.
 */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#if defined(__APPLE__) && defined(__x86_64__)
#include <dlfcn.h>
#include <mach-o/dyld.h>
#include <mach-o/loader.h>
#include <unistd.h>

typedef int32_t VkResult;
typedef struct VkInstance_T *VkInstance;
typedef struct VkPhysicalDevice_T *VkPhysicalDevice;
typedef void (*PFN_vkVoidFunction)(void);
typedef struct {
    int32_t sType;
    const void *pNext;
    const char *pApplicationName;
    uint32_t applicationVersion;
    const char *pEngineName;
    uint32_t engineVersion;
    uint32_t apiVersion;
} VkApplicationInfo;
typedef struct {
    int32_t sType;
    const void *pNext;
    uint32_t flags;
    const VkApplicationInfo *pApplicationInfo;
    uint32_t enabledLayerCount;
    const char *const *ppEnabledLayerNames;
    uint32_t enabledExtensionCount;
    const char *const *ppEnabledExtensionNames;
} VkInstanceCreateInfo;
typedef struct {
    char extensionName[256];
    uint32_t specVersion;
} VkExtensionProperties;
typedef PFN_vkVoidFunction (*PFN_vkGetInstanceProcAddr)(VkInstance, const char *);
typedef VkResult (*PFN_vkEnumerateInstanceExtensionProperties)(const char *, uint32_t *, VkExtensionProperties *);
typedef VkResult (*PFN_vkCreateInstance)(const VkInstanceCreateInfo *, const void *, VkInstance *);
typedef VkResult (*PFN_vkEnumeratePhysicalDevices)(VkInstance, uint32_t *, VkPhysicalDevice *);
typedef void (*PFN_vkDestroyInstance)(VkInstance, const void *);

_Static_assert(sizeof(VkApplicationInfo) == 48, "Darwin x64 Vulkan application ABI");
_Static_assert(sizeof(VkInstanceCreateInfo) == 64, "Darwin x64 Vulkan instance ABI");
_Static_assert(sizeof(VkExtensionProperties) == 260, "Vulkan extension ABI");

static double start_time;

static double monotonic_seconds(void)
{
    struct timespec value;
    if (clock_gettime(CLOCK_MONOTONIC, &value)) return 0;
    return (double)value.tv_sec + (double)value.tv_nsec / 1000000000.0;
}

static void json_string(const char *value)
{
    const unsigned char *p = (const unsigned char *)(value ? value : "");
    putchar('"');
    for (; *p; ++p)
    {
        if (*p == '"' || *p == '\\') { putchar('\\'); putchar(*p); }
        else if (*p < 0x20) printf("\\u%04x", *p);
        else putchar(*p);
    }
    putchar('"');
}

static void stage(const char *name, const char *state, int32_t result)
{
    printf("DUSTORE_NATIVE_VULKAN {\"stage\":"); json_string(name);
    printf(",\"state\":"); json_string(state);
    printf(",\"result\":%d,\"elapsedSeconds\":%.6f}\n", result, monotonic_seconds() - start_time);
    fflush(stdout);
}

static void emit_images(const char *owned_root)
{
    uint32_t count = _dyld_image_count();
    size_t root_size = strlen(owned_root);
    for (uint32_t i = 0; i < count; ++i)
    {
        const char *name = _dyld_get_image_name(i);
        const struct mach_header *header = _dyld_get_image_header(i);
        int owned = name && !strncmp(name, owned_root, root_size) && name[root_size] == '/';
        if (!name || !header || (!owned && !strstr(name, "Metal") && !strstr(name, "IOGPU") &&
                                !strstr(name, "libsystem_kernel") && !strstr(name, "libsystem_c."))) continue;
        const unsigned char *cursor = (const unsigned char *)header +
            (header->magic == MH_MAGIC_64 ? sizeof(struct mach_header_64) : sizeof(struct mach_header));
        const unsigned char *end = cursor + header->sizeofcmds;
        char uuid[33] = {0};
        for (uint32_t j = 0; j < header->ncmds && j < 4096; ++j)
        {
            if (cursor + sizeof(struct load_command) > end) break;
            const struct load_command *command = (const struct load_command *)cursor;
            if (command->cmdsize < sizeof(*command) || cursor + command->cmdsize > end) break;
            if (command->cmd == LC_UUID && command->cmdsize >= sizeof(struct uuid_command))
            {
                const struct uuid_command *value = (const struct uuid_command *)command;
                for (unsigned int k = 0; k < 16; ++k) snprintf(uuid + 2 * k, 3, "%02x", value->uuid[k]);
                break;
            }
            cursor += command->cmdsize;
        }
        printf("DUSTORE_NATIVE_VULKAN {\"stage\":\"loaded-image\",\"path\":"); json_string(name);
        printf(",\"owned\":%s,\"uuid\":", owned ? "true" : "false"); json_string(uuid);
        printf("}\n");
    }
    fflush(stdout);
}

int main(int argc, char **argv)
{
    if (argc != 3) { fprintf(stderr, "Usage: native_vulkan_probe <owned-library> <owned-root>\n"); return 64; }
    start_time = monotonic_seconds();
    setvbuf(stdout, NULL, _IOLBF, 0);
    printf("DUSTORE_NATIVE_VULKAN {\"stage\":\"process-identity\",\"pid\":%ld,\"executable\":", (long)getpid());
    json_string(argv[0]); printf("}\n"); fflush(stdout);
    stage("dlopen", "begin", 0);
    void *library = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!library)
    {
        printf("DUSTORE_NATIVE_VULKAN {\"stage\":\"dlopen\",\"state\":\"error\",\"error\":");
        json_string(dlerror()); printf("}\n"); fflush(stdout); return 2;
    }
    stage("dlopen", "end", 0);
    emit_images(argv[2]);
    PFN_vkGetInstanceProcAddr get_proc = (PFN_vkGetInstanceProcAddr)dlsym(library, "vkGetInstanceProcAddr");
    if (!get_proc) { stage("vkGetInstanceProcAddr", "missing", -1); dlclose(library); return 3; }
    PFN_vkEnumerateInstanceExtensionProperties extensions =
        (PFN_vkEnumerateInstanceExtensionProperties)get_proc(NULL, "vkEnumerateInstanceExtensionProperties");
    PFN_vkCreateInstance create = (PFN_vkCreateInstance)get_proc(NULL, "vkCreateInstance");
    if (!extensions || !create) { stage("global-entry-points", "missing", -1); dlclose(library); return 3; }
    stage("vkEnumerateInstanceExtensionProperties", "begin", 0);
    uint32_t extension_count = 0;
    VkResult result = extensions(NULL, &extension_count, NULL);
    if (result || extension_count > 16384) { stage("vkEnumerateInstanceExtensionProperties", "error", result); dlclose(library); return 4; }
    VkExtensionProperties *properties = calloc(extension_count ? extension_count : 1, sizeof(*properties));
    if (!properties) { stage("extension-allocation", "error", -1); dlclose(library); return 4; }
    result = extensions(NULL, &extension_count, properties);
    if (result) { stage("vkEnumerateInstanceExtensionProperties", "error", result); free(properties); dlclose(library); return 4; }
    const char *requested[] = {"VK_KHR_external_memory_capabilities", "VK_KHR_external_semaphore_capabilities",
                              "VK_KHR_get_physical_device_properties2",
                              "VK_KHR_portability_enumeration"};
    const char *enabled[4];
    uint32_t enabled_count = 0;
    uint32_t flags = 0;
    for (uint32_t i = 0; i < extension_count; ++i)
    {
        properties[i].extensionName[255] = 0;
        printf("DUSTORE_NATIVE_VULKAN {\"stage\":\"instance-extension\",\"name\":");
        json_string(properties[i].extensionName); printf(",\"specVersion\":%u}\n", properties[i].specVersion);
    }
    for (unsigned int j = 0; j < sizeof(requested) / sizeof(requested[0]); ++j)
    {
        int found = 0;
        for (uint32_t i = 0; i < extension_count; ++i) if (!strcmp(properties[i].extensionName, requested[j])) { found = 1; break; }
        if (found) { enabled[enabled_count++] = requested[j]; if (j == 3) flags |= 1; }
        printf("DUSTORE_NATIVE_VULKAN {\"stage\":\"wine-requested-extension\",\"name\":"); json_string(requested[j]);
        printf(",\"available\":%s}\n", found ? "true" : "false");
    }
    free(properties);
    stage("vkEnumerateInstanceExtensionProperties", "end", 0);
    printf("DUSTORE_NATIVE_VULKAN {\"stage\":\"create-parameters\",\"apiVersion\":4194304,\"flags\":%u,\"extensionCount\":%u,\"logicalDevice\":false,\"surface\":false}\n", flags, enabled_count);
    VkApplicationInfo application = {0, NULL, "DUSTORE owned native ABI fixture", 0, NULL, 0, 4194304};
    VkInstanceCreateInfo info = {1, NULL, flags, &application, 0, NULL, enabled_count, enabled};
    VkInstance instance = NULL;
    stage("vkCreateInstance", "begin", 0);
    result = create(&info, NULL, &instance);
    stage("vkCreateInstance", "end", result);
    emit_images(argv[2]);
    if (result || !instance) { dlclose(library); return 5; }
    PFN_vkEnumeratePhysicalDevices devices = (PFN_vkEnumeratePhysicalDevices)get_proc(instance, "vkEnumeratePhysicalDevices");
    PFN_vkDestroyInstance destroy = (PFN_vkDestroyInstance)get_proc(instance, "vkDestroyInstance");
    if (!devices || !destroy) { stage("instance-entry-points", "missing", -1); dlclose(library); return 6; }
    stage("vkEnumeratePhysicalDevices", "begin", 0);
    uint32_t device_count = 0;
    result = devices(instance, &device_count, NULL);
    if (!result && device_count <= 256 && device_count)
    {
        VkPhysicalDevice *values = calloc(device_count, sizeof(*values));
        if (!values) result = -1;
        else { result = devices(instance, &device_count, values); free(values); }
    }
    if (device_count > 256) result = -1;
    printf("DUSTORE_NATIVE_VULKAN {\"stage\":\"physical-devices\",\"result\":%d,\"count\":%u,\"logicalDeviceCreated\":false}\n", result, device_count);
    stage("vkEnumeratePhysicalDevices", "end", result);
    stage("vkDestroyInstance", "begin", 0);
    destroy(instance, NULL);
    stage("vkDestroyInstance", "end", 0);
    dlclose(library);
    stage("complete", "end", result);
    return result ? 7 : 0;
}
#else
int main(void)
{
    fprintf(stderr, "This QA fixture requires native Darwin x86_64.\n");
    return 64;
}
#endif
