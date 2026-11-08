-- Stream notification extras: per-stream video messages, live messages edited in place, delete-on-end and the feed URL
-- social follows read from, plus the live messages being edited and a per-community embed colour.
-- Migration: 334-AddStreamExtrasAndServerColor.sql

ALTER TABLE "FollowedStreams"
    ADD COLUMN IF NOT EXISTS "VideoMessage"      TEXT,
    ADD COLUMN IF NOT EXISTS "UpdateLiveMessage" BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS "DeleteOnEnd"       BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS "SourceUrl"         TEXT;

CREATE TABLE IF NOT EXISTS "TrackedStreamMessages"
(
    "Id"               SERIAL PRIMARY KEY,
    "FollowedStreamId" INTEGER        NOT NULL UNIQUE,
    "GuildId"          NUMERIC(20, 0) NOT NULL,
    "ChannelId"        NUMERIC(20, 0) NOT NULL,
    "MessageId"        NUMERIC(20, 0) NOT NULL,
    "StreamKey"        TEXT           NOT NULL,
    "StartedAt"        TIMESTAMP      NOT NULL,
    "LastEdit"         TIMESTAMP      NOT NULL
);

CREATE INDEX IF NOT EXISTS "IX_TrackedStreamMessages_GuildId" ON "TrackedStreamMessages" ("GuildId");

CREATE TABLE IF NOT EXISTS "GuildEmbedColors"
(
    "Id"        SERIAL PRIMARY KEY,
    "GuildId"   NUMERIC(20, 0) NOT NULL UNIQUE,
    "OkColor"   INTEGER        NOT NULL,
    "DateAdded" TIMESTAMP      NOT NULL DEFAULT NOW()
);
