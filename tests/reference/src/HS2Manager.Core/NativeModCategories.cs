using System.Globalization;

namespace HS2Manager.Core;

internal static class NativeModCategories
{
    // Sideloader accepts both numbers and ChaListDefine.CategoryNo names in migrations.
    private static readonly Dictionary<string, int> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mo_head"] = 110, ["mt_skin_f"] = 111, ["mt_detail_f"] = 112, ["mt_beard"] = 121,
        ["mt_skin_b"] = 131, ["mt_detail_b"] = 132, ["mt_sunburn"] = 133,
        ["mo_top"] = 140, ["mo_bot"] = 141, ["mo_gloves"] = 144, ["mo_shoes"] = 147,
        ["fo_head"] = 210, ["ft_skin_f"] = 211, ["ft_detail_f"] = 212, ["ft_skin_b"] = 231, ["ft_detail_b"] = 232, ["ft_sunburn"] = 233,
        ["fo_top"] = 240, ["fo_bot"] = 241, ["fo_inner_t"] = 242, ["fo_inner_b"] = 243, ["fo_gloves"] = 244, ["fo_panst"] = 245, ["fo_socks"] = 246, ["fo_shoes"] = 247,
        ["so_hair_b"] = 300, ["so_hair_f"] = 301, ["so_hair_s"] = 302, ["so_hair_o"] = 303, ["st_hairmeshptn"] = 306,
        ["st_paint"] = 313, ["st_eyebrow"] = 314, ["st_eyelash"] = 315, ["st_eyeshadow"] = 316, ["st_eye"] = 317, ["st_eyeblack"] = 318, ["st_eye_hl"] = 319,
        ["st_cheek"] = 320, ["st_lip"] = 322, ["st_mole"] = 323, ["st_nip"] = 334, ["st_underhair"] = 335, ["st_pattern"] = 348,
        ["ao_none"] = 350, ["ao_head"] = 351, ["ao_ear"] = 352, ["ao_glasses"] = 353, ["ao_face"] = 354, ["ao_neck"] = 355,
        ["ao_shoulder"] = 356, ["ao_chest"] = 357, ["ao_waist"] = 358, ["ao_back"] = 359, ["ao_arm"] = 360, ["ao_hand"] = 361, ["ao_leg"] = 362, ["ao_kokan"] = 363
    };
    public static bool TryParse(string category, out int parsed) => int.TryParse(category, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) || Names.TryGetValue(category, out parsed);
    public static bool Matches(string category, int number) => TryParse(category, out int parsed) && parsed == number;
}
