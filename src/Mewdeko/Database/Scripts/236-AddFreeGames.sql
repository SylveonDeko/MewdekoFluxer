-- Free game alerts and the games already announced.
-- Migration: 236-AddFreeGames.sql

CREATE TABLE IF NOT EXISTS "FreeGameAlerts"
(
    "Id"        SERIAL PRIMARY KEY,
    "GuildId"   NUMERIC(20, 0) NOT NULL,
    "ChannelId" NUMERIC(20, 0) NOT NULL,
    "RoleId"    NUMERIC(20, 0),
    "Epic"      BOOLEAN        NOT NULL DEFAULT TRUE,
    "Steam"     BOOLEAN        NOT NULL DEFAULT TRUE,
    "Gog"       BOOLEAN        NOT NULL DEFAULT TRUE,
    "DateAdded" TIMESTAMP      NOT NULL DEFAULT NOW(),
    UNIQUE ("GuildId", "ChannelId")
);

CREATE TABLE IF NOT EXISTS "FreeGameAnnounced"
(
    "Id"        SERIAL PRIMARY KEY,
    "Store"     TEXT      NOT NULL,
    "GameKey"   TEXT      NOT NULL,
    "DateAdded" TIMESTAMP NOT NULL DEFAULT NOW(),
    UNIQUE ("Store", "GameKey")
);
