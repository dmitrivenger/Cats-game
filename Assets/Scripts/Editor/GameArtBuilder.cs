using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// Keeps Assets/Resources/GameArt.asset in sync with the art files on disk.
//  * Every PNG under the art folders is imported as a single, non-mipmapped Sprite.
//  * Whenever one of the catalogued files is (re)imported, the catalog is rebuilt, including
//    each sprite's visible-content rectangle (computed from the PNG's alpha).
//  * Menu: Kuzya > Rebuild Art Catalog forces a full rebuild.
// To add/replace art: put the PNG at the path listed below (or add a line here).
public class GameArtBuilder : AssetPostprocessor
{
    const string CatalogPath = "Assets/Resources/GameArt.asset";

    // key -> path (first existing path wins, so alternatives can be listed with '|')
    static readonly string[,] SpriteTable =
    {
        // cats: 5 colours, calm + angry
        { "cat_0", "Assets/Sprites/red.png" },           { "cat_0_an", "Assets/Sprites/red-an.png" },
        { "cat_1", "Assets/Sprites/white.png" },         { "cat_1_an", "Assets/Sprites/white_an.png" },
        { "cat_2", "Assets/Sprites/grey.png" },          { "cat_2_an", "Assets/Sprites/grey_an.png" },
        { "cat_3", "Assets/Sprites/black.png" },         { "cat_3_an", "Assets/Sprites/black-an.png" },
        { "cat_4", "Assets/Sprites/yellow_black.png" },  { "cat_4_an", "Assets/Sprites/yellow_black-an.png" },
        { "mint_face_0", "Assets/Sprites/mint_face_red.png" },
        { "mint_face_1", "Assets/Sprites/mint_face_white.png" },
        { "mint_face_2", "Assets/Sprites/mint_face_grey.png" },
        { "mint_face_3", "Assets/Sprites/mint_face_black.png" },
        { "mint_face_4", "Assets/Sprites/mint_face_yellow.png" },
        // special cats
        { "special_line", "Assets/Sprites/cat-cross.png" },
        { "special_bomb", "Assets/Sprites/cat-bomb.png" },
        { "special_rainbow", "Assets/Sprites/cat-rainbow.png" },
        { "special_hunter", "Assets/Sprites/Cat-hunter.png|Assets/Sprites/cat-hunter.png" },
        { "special_hunter_an", "Assets/Sprites/Cat-hunter-an.png|Assets/Sprites/cat-hunter-an.png|Assets/Sprites/Cat-hunter.png" },
        // blockers
        { "box_full", "Assets/Sprites/Cat-blockers/box_full.png" },
        { "box_damaged", "Assets/Sprites/Cat-blockers/box_damaged.png" },
        // box split in two along the rim: back half is drawn behind the cat, front half in front
        { "box_full_back", "Assets/Sprites/Cat-blockers/split/box_full_back.png" }, { "box_full_front", "Assets/Sprites/Cat-blockers/split/box_full_front.png" },
        { "box_damaged_back", "Assets/Sprites/Cat-blockers/split/box_damaged_back.png" }, { "box_damaged_front", "Assets/Sprites/Cat-blockers/split/box_damaged_front.png" },
        { "yarn_wrap", "Assets/Sprites/Cat-blockers/yarn_wrap.png" },
        { "yarn_ball", "Assets/Sprites/Cat-blockers/yarn_ball.png" },
        { "mint_wreath", "Assets/Sprites/Cat-blockers/mint_wreath.png" },
        // treasure chest (every 5 levels), daily "three bowls", milk x2 sticker
        { "chest_closed", "Assets/ui/Chest/chest_closed.png" }, { "chest_open", "Assets/ui/Chest/chest_open.png" }, { "map_chest", "Assets/Level-menu/map_chest.png" },
        { "daily_bowl", "Assets/ui/Daily/daily_bowl.png" }, { "daily_fish", "Assets/ui/Daily/daily_fish.png" }, { "daily_icon", "Assets/ui/Daily/daily_icon.png" },
        { "milk_x2", "Assets/Sprites/Milk/milk_x2.png" },
        { "mouse_idle", "Assets/Sprites/Mouse/mouse_idle.png" }, { "mouse_run", "Assets/Sprites/Mouse/mouse_run.png" }, { "mouse_caught", "Assets/Sprites/Mouse/mouse_caught.png" }, { "caught_sack", "Assets/Sprites/Mouse/caught_sack.png" },
        { "caught_word", "Assets/Combos/Caught/caught_word.png" }, { "caught_paw", "Assets/Combos/Caught/caught_paw.png" }, { "badge_meet", "Assets/ui/badge_meet.png" },
        // milk bucket (static, tips over), its puddle and splashes
        { "milk_bucket", "Assets/Sprites/Milk/milk_bucket.png" }, { "milk_bucket_tipped", "Assets/Sprites/Milk/milk_bucket_tipped.png" }, { "milk_puddle", "Assets/Sprites/Milk/milk_puddle.png" },
        { "milk_splash_1", "Assets/Sprites/FX/Milk/milk_splash_1.png" }, { "milk_splash_2", "Assets/Sprites/FX/Milk/milk_splash_2.png" }, { "milk_splash_3", "Assets/Sprites/FX/Milk/milk_splash_3.png" },
        { "milk_splash_4", "Assets/Sprites/FX/Milk/milk_splash_4.png" }, { "milk_splash_5", "Assets/Sprites/FX/Milk/milk_splash_5.png" }, { "milk_splash_6", "Assets/Sprites/FX/Milk/milk_splash_6.png" },
        // soap foam tiles under the cats (1 and 2 layers), goal icon, bubbles for the pop effect
        { "tile_foam_1", "Assets/Sprites/Foam/tile_foam_1.png" }, { "tile_foam_2", "Assets/Sprites/Foam/tile_foam_2.png" }, { "goal_foam", "Assets/Sprites/Foam/goal_foam.png" },
        { "bubble_1", "Assets/Sprites/FX/Foam/bubble_1.png" }, { "bubble_2", "Assets/Sprites/FX/Foam/bubble_2.png" }, { "bubble_3", "Assets/Sprites/FX/Foam/bubble_3.png" }, { "bubble_4", "Assets/Sprites/FX/Foam/bubble_4.png" },
        { "bubble_5", "Assets/Sprites/FX/Foam/bubble_5.png" }, { "bubble_6", "Assets/Sprites/FX/Foam/bubble_6.png" }, { "bubble_7", "Assets/Sprites/FX/Foam/bubble_7.png" }, { "bubble_8", "Assets/Sprites/FX/Foam/bubble_8.png" },
        { "mint_leaf", "Assets/Sprites/Cat-blockers/mint_leaf.png" },
        { "dizzy_stars", "Assets/Sprites/Cat-blockers/dizzy_stars.png" },
        { "vase", "Assets/Sprites/Cat-blockers/vase.png" },
        { "vase_cracked", "Assets/Sprites/Cat-blockers/vase_cracked.png" },
        { "fish_bowl", "Assets/Sprites/Cat-blockers/fish_bowl.png" },
        { "fish", "Assets/Sprites/Cat-blockers/fish.png" },
        { "bowl_exit", "Assets/Sprites/Cat-blockers/bowl_exit.png" },
        // debris
        { "box_piece_1", "Assets/Sprites/FX/box_piece_1.png" }, { "box_piece_2", "Assets/Sprites/FX/box_piece_2.png" }, { "box_piece_3", "Assets/Sprites/FX/box_piece_3.png" },
        { "vase_shard_1", "Assets/Sprites/FX/vase_shard_1.png" }, { "vase_shard_2", "Assets/Sprites/FX/vase_shard_2.png" }, { "vase_shard_3", "Assets/Sprites/FX/vase_shard_3.png" },
        { "petal_1", "Assets/Sprites/FX/petal_1.png" }, { "petal_2", "Assets/Sprites/FX/petal_2.png" }, { "petal_3", "Assets/Sprites/FX/petal_3.png" }, { "petal_4", "Assets/Sprites/FX/petal_4.png" },
        { "water_drop_1", "Assets/Sprites/FX/water_drop_1.png" }, { "water_drop_2", "Assets/Sprites/FX/water_drop_2.png" }, { "water_drop_3", "Assets/Sprites/FX/water_drop_3.png" },
        { "yarn_thread_1", "Assets/Sprites/FX/yarn_thread_1.png" }, { "yarn_thread_2", "Assets/Sprites/FX/yarn_thread_2.png" },
        // power-ups
        { "pu_paw", "Assets/Sprites/power-ups/power-up-paw.png" },
        { "pu_bomb", "Assets/Sprites/power-ups/power-up-bomb.png" },
        { "pu_color", "Assets/Sprites/power-ups/power-up-color.png" },
        { "pu_moves", "Assets/ui/moves 1.png|Assets/Sprites/power-ups/power-up-moves.png" },
        // combos
        { "combo_1", "Assets/Combos/soft/Combo.png|Assets/Combos/Combo.png" }, { "combo_2", "Assets/Combos/soft/super_kombo.png|Assets/Combos/super_kombo.png" },
        { "combo_3", "Assets/Combos/soft/mega_kombo.png|Assets/Combos/mega_kombo.png" }, { "combo_4", "Assets/Combos/soft/monster_kombo.png|Assets/Combos/monster_kombo.png" },
        // ui
        { "ui_panel", "Assets/ui/hud_panel3.png" }, { "ui_balance", "Assets/ui/balance.png" }, { "ui_moves", "Assets/ui/moves.png" },
        { "ui_check", "Assets/ui/check.png" },
        { "ui_play", "Assets/ui/play.png" }, { "ui_play_p", "Assets/ui/play_pressed.png" },
        { "ui_pause", "Assets/ui/pause.png" }, { "ui_pause_p", "Assets/ui/pause-pressed.png" },
        { "ui_next", "Assets/ui/next.png" }, { "ui_next_p", "Assets/ui/next-pressed.png" },
        { "ui_replay", "Assets/ui/replay.png" }, { "ui_replay_p", "Assets/ui/replay-pressed.png" },
        { "ui_sett", "Assets/ui/sett.png" }, { "ui_sett_p", "Assets/ui/sett-pressed.png" },
        { "ui_levels", "Assets/ui/level_select.png" }, { "ui_levels_p", "Assets/ui/Level_selec-pressed.png" },
        // optional art (not in the project yet - picked up automatically when added)
        { "ui_popup_panel", "Assets/ui/popup_panel.png" },
        { "ui_close", "Assets/ui/Close.png|Assets/ui/close.png" }, { "ui_close_p", "Assets/ui/Close-pressed.png|Assets/ui/close-pressed.png" },
        { "ui_home", "Assets/ui/home.png" },
        { "ui_score", "Assets/ui/score.png" },
        { "ui_music_on", "Assets/ui/music_on.png" }, { "ui_music_off", "Assets/ui/music_off.png" },
        { "ui_sound_on", "Assets/ui/sound_on.png" }, { "ui_sound_off", "Assets/ui/sound_off.png" },
        { "banner_win", "Assets/ui/win.png|Assets/ui/win_banner.png" }, { "banner_lose", "Assets/ui/lose.png|Assets/ui/lose_banner.png" },
        { "result_win", "Assets/ui/win_cats.png" }, { "result_lose", "Assets/ui/lose_cats.png" },
        { "cell_tile", "Assets/ui/cell_tile.png|Assets/Sprites/Background/cell_tile.png" },
        { "app_icon", "Assets/ui/logo.png|Assets/ui/app_icon.png" },
        { "fx_life", "Assets/ui/life.png" },
        // level map (Assets/Level-menu)
        { "map_node_done", "Assets/Level-menu/map_node_done.png" }, { "map_node_current", "Assets/Level-menu/map_node_current.png" }, { "map_node_locked", "Assets/Level-menu/map_node_locked.png" },
        { "map_marker", "Assets/Level-menu/map_marker.png" },
        { "map_decor_1", "Assets/Level-menu/map_decor_1.png" }, { "map_decor_2", "Assets/Level-menu/map_decor_2.png" }, { "map_decor_3", "Assets/Level-menu/map_decor_3.png" }, { "map_decor_4", "Assets/Level-menu/map_decor_4.png" },
        { "map_decor_5", "Assets/Level-menu/map_decor_5.png" }, { "map_decor_6", "Assets/Level-menu/map_decor_6.png" }, { "map_decor_7", "Assets/Level-menu/map_decor_7.png" }, { "map_decor_8", "Assets/Level-menu/map_decor_8.png" },
        { "map_decor_9", "Assets/Level-menu/map_decor_9.png" }, { "map_decor_10", "Assets/Level-menu/map_decor_10.png" }, { "map_decor_11", "Assets/Level-menu/map_decor_11.png" }, { "map_decor_12", "Assets/Level-menu/map_decor_12.png" },
        { "map_decor_13", "Assets/Level-menu/map_decor_13.png" }, { "map_decor_14", "Assets/Level-menu/map_decor_14.png" }, { "map_decor_15", "Assets/Level-menu/map_decor_15.png" }, { "map_decor_16", "Assets/Level-menu/map_decor_16.png" },
        { "map_flower_1", "Assets/Level-menu/map_flower_1.png" }, { "map_flower_2", "Assets/Level-menu/map_flower_2.png" }, { "map_flower_3", "Assets/Level-menu/map_flower_3.png" }, { "map_flower_4", "Assets/Level-menu/map_flower_4.png" }, { "map_flower_5", "Assets/Level-menu/map_flower_5.png" }, { "map_flower_6", "Assets/Level-menu/map_flower_6.png" },
        { "ui_booster_glow", "Assets/ui/booster_glow.png" }, { "ui_booster_sparkle", "Assets/ui/booster_sparkle.png" },
        // layered combo banner (Combos/Layers)
        { "cmb_word_1", "Assets/Combos/Layers/combo_word_1.png" }, { "cmb_word_2", "Assets/Combos/Layers/combo_word_2.png" }, { "cmb_word_3", "Assets/Combos/Layers/combo_word_3.png" }, { "cmb_word_4", "Assets/Combos/Layers/combo_word_4.png" }, { "cmb_paw", "Assets/Combos/Layers/combo_paw.png" }, { "cmb_paw_monster", "Assets/Combos/Layers/combo_paw_monster.png" }, { "cmb_rocket", "Assets/Combos/Layers/combo_rocket.png" }, { "cmb_star", "Assets/Combos/Layers/combo_star.png" }, { "cmb_monster", "Assets/Combos/Layers/combo_monster.png" }, { "cmb_slash", "Assets/Combos/Layers/combo_slash.png" }, { "cmb_burst", "Assets/Combos/Layers/combo_burst.png" }, { "cmb_bit_1", "Assets/Combos/Layers/combo_bit_1.png" }, { "cmb_bit_2", "Assets/Combos/Layers/combo_bit_2.png" }, { "cmb_bit_3", "Assets/Combos/Layers/combo_bit_3.png" }, { "cmb_bit_4", "Assets/Combos/Layers/combo_bit_4.png" }, { "cmb_bit_5", "Assets/Combos/Layers/combo_bit_5.png" }, { "cmb_bit_6", "Assets/Combos/Layers/combo_bit_6.png" }, { "cmb_bit_7", "Assets/Combos/Layers/combo_bit_7.png" }, { "cmb_bit_8", "Assets/Combos/Layers/combo_bit_8.png" },
        // result screen backdrops (ui/Result)
        { "res_win_rays", "Assets/ui/Result/win_rays.png" }, { "res_win_firework", "Assets/ui/Result/win_firework.png" }, { "res_win_cats", "Assets/ui/Result/win_cats.png" }, { "res_lose_cloud", "Assets/ui/Result/lose_cloud.png" }, { "res_lose_drop", "Assets/ui/Result/lose_drop.png" }, { "res_lose_splash", "Assets/ui/Result/lose_splash.png" }, { "res_lose_cats", "Assets/ui/Result/lose_cats.png" }, { "res_confetti_1", "Assets/ui/Result/win_confetti_1.png" }, { "res_confetti_2", "Assets/ui/Result/win_confetti_2.png" }, { "res_confetti_3", "Assets/ui/Result/win_confetti_3.png" }, { "res_confetti_4", "Assets/ui/Result/win_confetti_4.png" }, { "res_confetti_5", "Assets/ui/Result/win_confetti_5.png" }, { "res_confetti_6", "Assets/ui/Result/win_confetti_6.png" }, { "res_confetti_7", "Assets/ui/Result/win_confetti_7.png" }, { "res_confetti_8", "Assets/ui/Result/win_confetti_8.png" },
        // regular cat pop (Sprites/FX/Burn/regular) - white layers are tinted per cat colour in code
        { "fx_flash", "Assets/Sprites/FX/Burn/regular/fx_flash.png" }, { "fx_ring", "Assets/Sprites/FX/Burn/regular/fx_ring.png" },
        { "fx_puff", "Assets/Sprites/FX/Burn/regular/fx_puff.png" }, { "fx_sparkle", "Assets/Sprites/FX/Burn/regular/fx_sparkle.png" },
        { "fx_fur_1", "Assets/Sprites/FX/Burn/regular/pieces/fx_fur_1.png" }, { "fx_fur_2", "Assets/Sprites/FX/Burn/regular/pieces/fx_fur_2.png" },
        { "fx_fur_3", "Assets/Sprites/FX/Burn/regular/pieces/fx_fur_3.png" }, { "fx_fur_4", "Assets/Sprites/FX/Burn/regular/pieces/fx_fur_4.png" },
        { "fx_heart", "Assets/Sprites/FX/Burn/regular/pieces/fx_heart.png" }, { "fx_paw", "Assets/Sprites/FX/Burn/regular/pieces/fx_paw.png" },
        // Bomb cat blast (Sprites/FX/Burn/Bomb) - white, tinted in code
        { "bomb_flash", "Assets/Sprites/FX/Burn/Bomb/bomb_flash.png" }, { "bomb_rays", "Assets/Sprites/FX/Burn/Bomb/bomb_rays.png" }, { "bomb_ring", "Assets/Sprites/FX/Burn/Bomb/bomb_ring.png" },
        { "bomb_fireball", "Assets/Sprites/FX/Burn/Bomb/bomb_fireball.png" }, { "bomb_scorch", "Assets/Sprites/FX/Burn/Bomb/bomb_scorch.png" }, { "bomb_smoke_1", "Assets/Sprites/FX/Burn/Bomb/bomb_smoke_1.png" },
        { "bomb_smoke_2", "Assets/Sprites/FX/Burn/Bomb/bomb_smoke_2.png" }, { "bomb_smoke_3", "Assets/Sprites/FX/Burn/Bomb/bomb_smoke_3.png" }, { "bomb_smoke_4", "Assets/Sprites/FX/Burn/Bomb/bomb_smoke_4.png" },
        { "bomb_ember_1", "Assets/Sprites/FX/Burn/Bomb/bomb_ember_1.png" }, { "bomb_ember_2", "Assets/Sprites/FX/Burn/Bomb/bomb_ember_2.png" }, { "bomb_ember_3", "Assets/Sprites/FX/Burn/Bomb/bomb_ember_3.png" },
        { "bomb_ember_4", "Assets/Sprites/FX/Burn/Bomb/bomb_ember_4.png" },
        // Rainbow cat blast (Sprites/FX/Burn/Rainbow) - white, rainbow hue cycled in code
        { "rainbow_swirl", "Assets/Sprites/FX/Burn/Rainbow/rainbow_swirl.png" }, { "rainbow_orb", "Assets/Sprites/FX/Burn/Rainbow/rainbow_orb.png" }, { "rainbow_target", "Assets/Sprites/FX/Burn/Rainbow/rainbow_target.png" },
        { "rainbow_wave", "Assets/Sprites/FX/Burn/Rainbow/rainbow_wave.png" }, { "rainbow_trail", "Assets/Sprites/FX/Burn/Rainbow/rainbow_trail.png" }, { "rainbow_glitter_1", "Assets/Sprites/FX/Burn/Rainbow/rainbow_glitter_1.png" },
        { "rainbow_glitter_2", "Assets/Sprites/FX/Burn/Rainbow/rainbow_glitter_2.png" }, { "rainbow_glitter_3", "Assets/Sprites/FX/Burn/Rainbow/rainbow_glitter_3.png" }, { "rainbow_glitter_4", "Assets/Sprites/FX/Burn/Rainbow/rainbow_glitter_4.png" },
        // Hunter cat (Sprites/FX/Burn/Hunter) - white layers tinted forest green; the arrow keeps its colours
        { "hunter_horn", "Assets/Sprites/FX/Burn/Hunter/hunter_horn.png" }, { "hunter_compass", "Assets/Sprites/FX/Burn/Hunter/hunter_compass.png" },
        { "hunter_needle", "Assets/Sprites/FX/Burn/Hunter/hunter_needle.png" }, { "hunter_crosshair", "Assets/Sprites/FX/Burn/Hunter/hunter_crosshair.png" },
        { "hunter_hit", "Assets/Sprites/FX/Burn/Hunter/hunter_hit.png" }, { "hunter_trail", "Assets/Sprites/FX/Burn/Hunter/hunter_trail.png" },
        { "hunter_arrow", "Assets/Sprites/FX/Burn/Hunter/hunter_arrow.png" },
        { "hunter_feather_1", "Assets/Sprites/FX/Burn/Hunter/hunter_feather_1.png" }, { "hunter_feather_2", "Assets/Sprites/FX/Burn/Hunter/hunter_feather_2.png" },
        { "hunter_feather_3", "Assets/Sprites/FX/Burn/Hunter/hunter_feather_3.png" }, { "hunter_feather_4", "Assets/Sprites/FX/Burn/Hunter/hunter_feather_4.png" },
        // booster FX (Sprites/FX/Burn/Boosters): paw / bomb / wand keep colours, the rest is white and tinted in code
        { "booster_paw", "Assets/Sprites/FX/Burn/Boosters/booster_paw.png" }, { "booster_bomb", "Assets/Sprites/FX/Burn/Boosters/booster_bomb.png" },
        { "booster_wand", "Assets/Sprites/FX/Burn/Boosters/booster_wand.png" }, { "booster_slap", "Assets/Sprites/FX/Burn/Boosters/booster_slap.png" },
        { "booster_starburst", "Assets/Sprites/FX/Burn/Boosters/booster_starburst.png" }, { "booster_magic_circle", "Assets/Sprites/FX/Burn/Boosters/booster_magic_circle.png" },
        // Line cat blast (Sprites/FX/Burn/Line) - white, tinted in code
        { "line_charge", "Assets/Sprites/FX/Burn/Line/line_charge.png|Assets/Sprites/FX/Burn/line/line_charge.png" }, { "line_beam", "Assets/Sprites/FX/Burn/Line/line_beam.png|Assets/Sprites/FX/Burn/line/line_beam.png" },
        { "line_head", "Assets/Sprites/FX/Burn/Line/line_head.png|Assets/Sprites/FX/Burn/line/line_head.png" }, { "line_claws", "Assets/Sprites/FX/Burn/Line/line_claws.png|Assets/Sprites/FX/Burn/line/line_claws.png" },
        { "line_spark_1", "Assets/Sprites/FX/Burn/Line/line_spark_1.png|Assets/Sprites/FX/Burn/line/line_spark_1.png" }, { "line_spark_2", "Assets/Sprites/FX/Burn/Line/line_spark_2.png|Assets/Sprites/FX/Burn/line/line_spark_2.png" },
        { "line_spark_3", "Assets/Sprites/FX/Burn/Line/line_spark_3.png|Assets/Sprites/FX/Burn/line/line_spark_3.png" }, { "line_spark_4", "Assets/Sprites/FX/Burn/Line/line_spark_4.png|Assets/Sprites/FX/Burn/line/line_spark_4.png" },
        // background + splash
        { "bg_pattern", "Assets/Sprites/Background/bg_pattern_tile.png" },
        { "bg_bokeh", "Assets/Sprites/Background/bg_bokeh.png" },
        { "bokeh_dot", "Assets/Sprites/Background/bokeh_dot.png" },
        { "splash", "Assets/Sprites/splash.png" }, { "splash_blur", "Assets/Sprites/splash_blur.png" },
        { "ui_play_big", "Assets/ui/play_big.png" }, { "ui_play_big_pressed", "Assets/ui/play_big_pressed.png" },
    };

    // sound key used in code -> file(s) in Assets/Sounds (first existing wins; '|' separates alternatives)
    static readonly Dictionary<string, string> SoundFiles = new Dictionary<string, string>
    {
        { "click", "Click|click" },                              // menu / button presses
        { "soap_1", "soap_1" }, { "soap_2", "soap_2" }, { "soap_3", "soap_3" },   // soap foam pops (three takes)
        { "milk_spill", "milk_spill" }, { "milk_lap", "milk_lap" },
        { "mouse_hop", "mouse_hop" }, { "mouse_steal", "mouse_steal" }, { "mouse_caught", "mouse_caught" }, { "mouse_run", "mouse_run" },
        { "chest_tap", "chest_tap" }, { "chest_open", "chest_open" }, { "meet_card", "meet_card" },
        { "explosion", "bomb|explosion" },                       // Bomb cat + Mega-bomb
        { "Line-Pop", "line-special|Line-Pop" },                  // Line cat
        { "Pop", "collision|Pop" },                              // regular matches
        { "Combo", "combo|Combo" },                              // combo notification ("Отлично!" banners)
        { "hunter", "hunter-speacial|hunter-special|hunter" },   // Hunter cat
        { "nope", "groaning|nope" },
        { "vase", "hit-vase|vase" },                              // vase breaks
        { "mint_off", "mint_off|mint" },                          // catnip cleared off a cat (just the rustle)
        { "freez", "mint|freez" },                                // catnip spreads to a neighbour (rustle + sleepy purr)
        { "Hit", "hit-cardboard|Hit" },                           // box hit (also a tap on a locked level on the map)                              // unsuccessful swipe (cats go back)
        { "swap_1", "swap_1" }, { "swap_2", "swap_2" }, { "swap_3", "swap_3" },   // successful swipe (3 takes, picked at random)
    };

    static readonly string[] SoundNames =
        { "Chain", "Combo", "Game", "Hit", "Line-Pop", "Pop", "balance", "click", "explosion", "freez", "hummer", "lost", "menu", "rainbow", "win" };

    static bool IsArtPath(string p)
    {
        p = p.Replace('\\', '/');
        return p.StartsWith("Assets/Sprites/") || p.StartsWith("Assets/ui/") || p.StartsWith("Assets/Combos/") || p.StartsWith("Assets/Blockers/") || p.StartsWith("Assets/Level-menu/");
    }

    // Sounds: music is streamed from disk at ~128 kbps (a 2-minute track no longer sits in memory as
    // ~20 MB of raw samples); short effects are mono and decompressed once at a lighter quality.
    void OnPreprocessAudio()
    {
        if (!assetPath.StartsWith("Assets/Sounds/")) return;
        var ai = (AudioImporter)assetImporter;
        string file = System.IO.Path.GetFileNameWithoutExtension(assetPath).ToLowerInvariant();
        bool music = file == "game" || file == "menu";
        var st = ai.defaultSampleSettings;
        st.compressionFormat = AudioCompressionFormat.Vorbis;
        st.quality = music ? 0.5f : 0.6f;
        st.loadType = music ? AudioClipLoadType.Streaming : AudioClipLoadType.DecompressOnLoad;
        ai.defaultSampleSettings = st;
        ai.forceToMono = !music;
    }

    void OnPreprocessTexture()
    {
        if (!IsArtPath(assetPath)) return;
        var ti = (TextureImporter)assetImporter;
        ti.textureType = TextureImporterType.Sprite;
        ti.spriteImportMode = SpriteImportMode.Single;
        ti.mipmapEnabled = false;
        ti.alphaIsTransparency = true;
        ti.sRGBTexture = true;
        var settings = new TextureImporterSettings();
        ti.ReadTextureSettings(settings);
        settings.spriteMeshType = SpriteMeshType.FullRect;
        settings.spriteAlignment = (int)SpriteAlignment.Center;
        ti.SetTextureSettings(settings);
        bool big = assetPath.EndsWith("splash.png") || assetPath.EndsWith("bg_bokeh.png") || assetPath.Contains("win_cats") || assetPath.Contains("lose_cats");
        // small effect particles never show bigger than ~a cell: 512 is plenty
        string lp = assetPath.ToLowerInvariant();
        bool fx = lp.Contains("/fx/") || lp.Contains("confetti") || lp.Contains("combo_bit") || lp.Contains("lose_drop") || lp.Contains("lose_splash");
        ti.maxTextureSize = big ? 2048 : fx ? 512 : 1024;
        // NOTE: block compression (DXT / ASTC) needs width and height that are multiples of 4 -
        // art PNGs are kept at such sizes (originals in _art_originals/), otherwise Unity silently
        // stores them uncompressed (4x bigger).
        if (assetPath.EndsWith("popup_panel.png"))
        {
            // 9-slice: ears+ribbon on top and tail+paws at the bottom keep their shape, only the
            // plain blue middle stretches when a window is taller or shorter.
            ti.maxTextureSize = 2048;
            settings.spriteBorder = new Vector4(0, 410, 0, 440);
            ti.SetTextureSettings(settings);
        }
        ti.wrapMode = assetPath.EndsWith("bg_pattern_tile.png") ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
        ti.textureCompression = TextureImporterCompression.Compressed;
        ti.filterMode = FilterMode.Bilinear;
    }

    static bool pending;
    static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        bool relevant = false;
        foreach (var p in imported) if (IsArtPath(p) || p.StartsWith("Assets/Sounds/") || p.StartsWith("Assets/Fonts/")) { relevant = true; break; }
        if (!relevant) foreach (var p in deleted) if (IsArtPath(p)) { relevant = true; break; }
        if (!relevant || pending) return;
        pending = true;
        EditorApplication.delayCall += () => { pending = false; Build(false); };
    }

    [MenuItem("Kuzya/Rebuild Art Catalog")]
    public static void RebuildMenu() { Build(true); }

    public static void Build(bool force)
    {
        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
        var art = AssetDatabase.LoadAssetAtPath<GameArt>(CatalogPath);
        bool created = false;
        if (art == null) { art = ScriptableObject.CreateInstance<GameArt>(); AssetDatabase.CreateAsset(art, CatalogPath); created = true; }

        var old = new Dictionary<string, GameArt.SpriteEntry>();
        foreach (var e in art.sprites) if (e != null && e.key != null) old[e.key] = e;

        var list = new List<GameArt.SpriteEntry>();
        int n = SpriteTable.GetLength(0);
        int missing = 0;
        for (int i = 0; i < n; i++)
        {
            string key = SpriteTable[i, 0];
            string path = null;
            foreach (var cand in SpriteTable[i, 1].Split('|')) if (File.Exists(cand)) { path = cand; break; }
            if (path == null) { missing++; continue; }
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null) continue;
            long stamp = File.GetLastWriteTimeUtc(path).Ticks;
            var entry = new GameArt.SpriteEntry { key = key, path = path, sprite = sprite, stamp = stamp };
            if (!force && old.TryGetValue(key, out var prev) && prev.path == path && prev.stamp == stamp)
                entry.content = prev.content;
            else
                entry.content = ComputeContent(path);
            list.Add(entry);
        }
        art.sprites = list;

        var clips = new List<GameArt.ClipEntry>();
        var keys = new List<string>(SoundNames);
        foreach (var k in SoundFiles.Keys) if (!keys.Contains(k)) keys.Add(k);
        foreach (var s in keys)
        {
            string files = SoundFiles.TryGetValue(s, out var f) ? f : s;
            AudioClip clip = null;
            foreach (var name in files.Split('|'))
            {
                foreach (var ext in new[] { ".mp3", ".wav", ".ogg" })
                {
                    clip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Sounds/" + name + ext);
                    if (clip != null) break;
                }
                if (clip != null) break;
            }
            if (clip != null) clips.Add(new GameArt.ClipEntry { key = s, clip = clip });
        }
        art.clips = clips;
        art.displayFont = AssetDatabase.LoadAssetAtPath<Font>("Assets/Fonts/Marmelad-Regular.ttf");
        art.numberFont = AssetDatabase.LoadAssetAtPath<Font>("Assets/Fonts/Nunito-Black.ttf");
        art.InvalidateMaps();
        EditorUtility.SetDirty(art);
        AssetDatabase.SaveAssets();
        Debug.Log($"[GameArt] catalog {(created ? "created" : "updated")}: {list.Count} sprites, {clips.Count} sounds, {missing} optional/missing entries.");
    }

    // Visible part of the PNG (alpha > 24) in normalized coordinates, y measured from the bottom
    // like Unity textures.
    static Rect ComputeContent(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!tex.LoadImage(bytes)) { Object.DestroyImmediate(tex); return new Rect(0, 0, 1, 1); }
        int w = tex.width, h = tex.height;
        var px = tex.GetPixels32();
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y += 2)
        {
            int row = y * w;
            for (int x = 0; x < w; x += 2)
            {
                if (px[row + x].a > 24)
                {
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
            }
        }
        Object.DestroyImmediate(tex);
        if (maxX < 0) return new Rect(0, 0, 1, 1);
        return new Rect(minX / (float)w, minY / (float)h, (maxX - minX + 2) / (float)w, (maxY - minY + 2) / (float)h);
    }
}
