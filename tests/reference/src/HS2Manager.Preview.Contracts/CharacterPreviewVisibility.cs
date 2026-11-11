using System;

namespace HS2Manager.Preview.Contracts
{
    // CustomBase.ChangeClothesState's three maker presets. This is transient
    // viewport state, never a change to the character's saved outfit.
    [Serializable]
    public sealed class CharacterPreviewVisibility
    {
        public int Clothing;
        public bool Accessories = true;

        public bool ClothesVisible(int slot)
        {
            return Clothing == 0 || Clothing == 1 && slot >= 2 && slot <= 6;
        }
    }
}
