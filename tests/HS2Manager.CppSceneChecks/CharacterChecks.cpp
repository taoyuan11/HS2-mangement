#include "Character/CharacterPreparation.h"
#include <iostream>
using namespace hs2;

int wmain(int argc, wchar_t **argv)
{
    try
    {
        if (argc != 5)
        {
            std::cerr << "Usage: hs2_character_checks game data classdata.tpk card.png\n";
            return 2;
        }
        auto card = CardParser().Parse(argv[4]);
        if (card.Status != CardStatus::Valid)
            throw std::runtime_error(card.Error);
        NativeCharacterService service(argv[1], argv[2], argv[3]);
        auto prepared = service.Prepare(card, nullptr, {},
                                        [](auto &p)
                                        {
                                            std::cout << p.Message << " " << p.Completed << "/" << p.Total << std::endl;
                                        });
        auto &scene = *prepared.Scene;
        std::cout << "Character=" << scene.Name << " parts=" << scene.Parts.size() << " nodes=" << scene.Nodes.size()
                  << " renderers=" << scene.Renderers.size() << " bones=" << scene.Bones.size()
                  << " selections=" << scene.Definition->Selections.size() << "\n";
        for (auto &message : scene.Diagnostics)
            std::cout << "diagnostic: " << message << "\n";
        size_t vertices = 0;
        for (auto &renderer : scene.Renderers)
        {
            renderer->Mesh->Validate();
            vertices += renderer->Mesh->VertexCount;
        }
        std::cout << "Character preparation passed; vertices=" << vertices << "\n";
        return 0;
    }
    catch (const std::exception &e)
    {
        std::cerr << e.what() << "\n";
        return 1;
    }
}
