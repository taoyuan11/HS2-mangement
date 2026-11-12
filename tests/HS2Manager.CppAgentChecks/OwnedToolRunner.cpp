#include "Tools/OwnedToolProcess.h"
#include <iostream>

int wmain(int argc, wchar_t **argv)
{
    if (argc < 5)
        return 2;
    try
    {
        std::vector<std::wstring> arguments;
        for (int i = 5; i < argc; ++i)
            arguments.emplace_back(argv[i]);
        return hs2::RunOwnedTool(argv[1], arguments, argv[2], std::chrono::seconds(std::stoi(argv[4])), argv[3]);
    }
    catch (const std::exception &error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
