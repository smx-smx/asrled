#pragma once

#include <string>
#include <vector>
#include <cassert>

namespace util
{
    template< typename... Args >
    std::string ssprintf(const char *format, Args... args) {
        int length = std::snprintf(nullptr, 0, format, args...);
        assert(length >= 0);

        std::vector<char> buf(length + 1);
        std::snprintf(buf.data(), length + 1, format, args...);

        std::string str(buf.data());
        return str;
    }

    void hexdump(std::ostream &out, void *pData, size_t length);
}