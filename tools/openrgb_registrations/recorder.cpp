// Records OpenRGB's detector registrations as its own static detector objects
// make them (#557). Linked with one family's own *Detect.cpp files, built from
// OpenRGB unchanged, in place of DetectionManager.cpp: the detector classes of
// DetectionManager.h call DetectionManager::get()->Register...() from their
// constructors, which run before main, and these definitions write down the
// arguments instead of keeping a detector. No detector function is called.
// tools/openrgb_registrations/build.py builds and runs it.
#include <cstdio>
#include <string>
#include <vector>
#include "DetectionManager.h"

static std::vector<std::string>& rows()
{
    static std::vector<std::string> r;
    return r;
}

static void add(const char* kind, const std::string& name, int vid, int pid, int interface_number, int usage_page,
                int usage, int bus, bool enabled)
{
    char line[512];
    snprintf(line, sizeof(line), "%s\t%s\t%d\t%d\t%d\t%d\t%d\t%d\t%d", kind, name.c_str(), vid, pid, interface_number,
             usage_page, usage, bus, enabled ? 1 : 0);
    rows().push_back(line);
}

// The instance is never touched: the member functions below use no member.
DetectionManager* DetectionManager::get()
{
    return reinterpret_cast<DetectionManager*>(&rows());
}

void DetectionManager::RegisterDeviceDetector(std::string name, DeviceDetectorFunction, bool enabled_by_default)
{
    add("Device", name, -1, -1, -1, -1, -1, -1, enabled_by_default);
}

void DetectionManager::RegisterDynamicDetector(std::string name, DynamicDetectorFunction)
{
    add("Dynamic", name, -1, -1, -1, -1, -1, -1, true);
}

void DetectionManager::RegisterHIDDeviceDetector(std::string name, HIDDeviceDetectorFunction, int vid, int pid,
                                                 int interface_number, int usage_page, int usage,
                                                 bool enabled_by_default, int bus)
{
    add("Hid", name, vid, pid, interface_number, usage_page, usage, bus, enabled_by_default);
}

void DetectionManager::RegisterHIDWrappedDeviceDetector(std::string name, HIDWrappedDeviceDetectorFunction, int vid,
                                                        int pid, int interface_number, int usage_page, int usage,
                                                        bool enabled_by_default, int bus)
{
    add("HidWrapped", name, vid, pid, interface_number, usage_page, usage, bus, enabled_by_default);
}

void DetectionManager::RegisterUSBSerialDeviceDetector(std::string name, USBSerialDeviceDetectorFunction, int vid,
                                                       int pid, bool enabled_by_default)
{
    add("UsbSerial", name, vid, pid, -1, -1, -1, -1, enabled_by_default);
}

void DetectionManager::RegisterPreDetectionHook(PreDetectionHookFunction)
{
}

int main(int argc, char** argv)
{
    FILE* f = fopen(argv[1], "ab");
    if(f == nullptr)
    {
        return 1;
    }
    for(const std::string& row : rows())
    {
        fprintf(f, "%s\t%s\n", argv[2], row.c_str());
    }
    fclose(f);
    return 0;
}
