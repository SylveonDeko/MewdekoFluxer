UPDATE "TemplateBar"
SET "BarColor" = 'FFFFFFFF', "BarPointAx" = 287, "BarPointAy" = 106, "BarPointBx" = 253, "BarPointBy" = 229,
    "BarLength" = 443
WHERE "BarColor" = 'FF000000' AND "BarPointAx" = 319 AND "BarPointAy" = 119 AND "BarPointBx" = 284
  AND "BarPointBy" = 250 AND "BarLength" = 452;

UPDATE "TemplateClub"
SET "ClubIconX" = 737, "ClubIconY" = 24, "ClubIconSizeX" = 44, "ClubIconSizeY" = 44, "ClubNameColor" = 'FFFFFFFF',
    "ClubNameFontSize" = 20, "ClubNameX" = 595, "ClubNameY" = 70
WHERE "ClubIconX" = 717 AND "ClubIconY" = 37 AND "ClubIconSizeX" = 49 AND "ClubIconSizeY" = 49
  AND "ClubNameColor" = 'FF000000' AND "ClubNameFontSize" = 32 AND "ClubNameX" = 649 AND "ClubNameY" = 50;

UPDATE "TemplateGuild"
SET "GuildLevelColor" = 'FFFFFFFF', "GuildLevelFontSize" = 24, "GuildLevelX" = 41, "GuildLevelY" = 190,
    "GuildRankColor" = 'FFFFFFFF', "GuildRankFontSize" = 24, "GuildRankX" = 150, "GuildRankY" = 197
WHERE "GuildLevelColor" = 'FF000000' AND "GuildLevelFontSize" = 27 AND "GuildLevelX" = 42 AND "GuildLevelY" = 206
  AND "GuildRankColor" = 'FF000000' AND "GuildRankFontSize" = 25 AND "GuildRankX" = 148 AND "GuildRankY" = 211;

UPDATE "TemplateUser"
SET "TextColor" = 'FFFFFFFF', "FontSize" = 44, "TextX" = 100, "TextY" = 58, "IconX" = 10, "IconY" = 5,
    "IconSizeX" = 72, "IconSizeY" = 72
WHERE "TextColor" = 'FF000000' AND "FontSize" = 50 AND "TextX" = 120 AND "TextY" = 70 AND "IconX" = 27
  AND "IconY" = 24 AND "IconSizeX" = 73 AND "IconSizeY" = 74;

UPDATE "Template"
SET "OutputSizeX" = 800, "OutputSizeY" = 246, "TimeOnLevelX" = 42, "TimeOnLevelY" = 226, "TimeOnLevelFontSize" = 18,
    "TimeOnLevelColor" = 'FFFFFFFF'
WHERE "OutputSizeX" = 797 AND "OutputSizeY" = 279 AND "TimeOnLevelX" = 50 AND "TimeOnLevelY" = 204
  AND "TimeOnLevelFontSize" = 20 AND "TimeOnLevelColor" = 'FF000000';
