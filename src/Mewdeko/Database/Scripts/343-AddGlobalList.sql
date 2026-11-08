-- Global list: communities that opt in report users, vote on each other's reports and act on approved entries.
-- Category: 0 Scamming, 1 Raiding, 2 SpamBot, 3 Harassment, 4 Nsfw, 5 Impersonation, 6 Other.
-- Action: 0 Ignore, 1 Alert, 2 QuarantineRole, 3 Kick, 4 Ban.
-- Entry status: 0 Pending, 1 Live, 2 Rejected, 3 Removed. Appeal status: 0 Open, 1 Approved, 2 Rejected.
-- Migration: 343-AddGlobalList.sql

CREATE TABLE IF NOT EXISTS "GlobalListSettings"
(
    "Id"               SERIAL PRIMARY KEY,
    "GuildId"          NUMERIC(20, 0) NOT NULL UNIQUE,
    "Enabled"          BOOLEAN        NOT NULL DEFAULT FALSE,
    "LogChannelId"     NUMERIC(20, 0),
    "QuarantineRoleId" NUMERIC(20, 0),
    "ApplyToExisting"  BOOLEAN        NOT NULL DEFAULT FALSE,
    "DateAdded"        TIMESTAMP      NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS "GlobalListCategoryActions"
(
    "Id"       SERIAL PRIMARY KEY,
    "GuildId"  NUMERIC(20, 0) NOT NULL,
    "Category" INTEGER        NOT NULL,
    "Action"   INTEGER        NOT NULL,
    UNIQUE ("GuildId", "Category")
);

CREATE TABLE IF NOT EXISTS "GlobalListEntries"
(
    "Id"               SERIAL PRIMARY KEY,
    "UserId"           NUMERIC(20, 0) NOT NULL,
    "Category"         INTEGER        NOT NULL,
    "Status"           INTEGER        NOT NULL DEFAULT 0,
    "DecisionNote"     TEXT,
    "DecidedBy"        NUMERIC(20, 0),
    "DecidedAt"        TIMESTAMP,
    "RemovedBy"        NUMERIC(20, 0),
    "RemovedAt"        TIMESTAMP,
    "RemovedNote"      TEXT,
    "RemovedByAppeal"  BOOLEAN        NOT NULL DEFAULT FALSE,
    "DateAdded"        TIMESTAMP      NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_GlobalListEntries_OpenUser"
    ON "GlobalListEntries" ("UserId") WHERE "Status" IN (0, 1);
CREATE INDEX IF NOT EXISTS "IX_GlobalListEntries_Status" ON "GlobalListEntries" ("Status");

CREATE TABLE IF NOT EXISTS "GlobalListReports"
(
    "Id"        SERIAL PRIMARY KEY,
    "EntryId"   INTEGER        NOT NULL REFERENCES "GlobalListEntries" ("Id") ON DELETE CASCADE,
    "GuildId"   NUMERIC(20, 0) NOT NULL,
    "Category"  INTEGER        NOT NULL,
    "Reason"    TEXT           NOT NULL,
    "Evidence"  TEXT           NOT NULL DEFAULT '',
    "DateAdded" TIMESTAMP      NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS "IX_GlobalListReports_EntryId" ON "GlobalListReports" ("EntryId");
CREATE INDEX IF NOT EXISTS "IX_GlobalListReports_Guild" ON "GlobalListReports" ("GuildId", "DateAdded");

CREATE TABLE IF NOT EXISTS "GlobalListEvidenceImages"
(
    "Id"        SERIAL PRIMARY KEY,
    "ReportId"  INTEGER   NOT NULL REFERENCES "GlobalListReports" ("Id") ON DELETE CASCADE,
    "Data"      BYTEA     NOT NULL,
    "DateAdded" TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS "IX_GlobalListEvidenceImages_ReportId" ON "GlobalListEvidenceImages" ("ReportId");

CREATE TABLE IF NOT EXISTS "GlobalListVotes"
(
    "Id"        SERIAL PRIMARY KEY,
    "EntryId"   INTEGER        NOT NULL REFERENCES "GlobalListEntries" ("Id") ON DELETE CASCADE,
    "GuildId"   NUMERIC(20, 0) NOT NULL,
    "Confirm"   BOOLEAN        NOT NULL,
    "Note"      TEXT,
    "DateAdded" TIMESTAMP      NOT NULL DEFAULT NOW(),
    UNIQUE ("EntryId", "GuildId")
);

CREATE TABLE IF NOT EXISTS "GlobalListReviewers"
(
    "Id"        SERIAL PRIMARY KEY,
    "UserId"    NUMERIC(20, 0) NOT NULL UNIQUE,
    "AddedBy"   NUMERIC(20, 0) NOT NULL,
    "DateAdded" TIMESTAMP      NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS "GlobalListStandings"
(
    "Id"        SERIAL PRIMARY KEY,
    "GuildId"   NUMERIC(20, 0) NOT NULL UNIQUE,
    "Approved"  INTEGER        NOT NULL DEFAULT 0,
    "Rejected"  INTEGER        NOT NULL DEFAULT 0,
    "ResetAt"   TIMESTAMP
);

CREATE TABLE IF NOT EXISTS "GlobalListAppeals"
(
    "Id"           SERIAL PRIMARY KEY,
    "EntryId"      INTEGER        NOT NULL REFERENCES "GlobalListEntries" ("Id") ON DELETE CASCADE,
    "UserId"       NUMERIC(20, 0) NOT NULL,
    "Text"         TEXT           NOT NULL,
    "Status"       INTEGER        NOT NULL DEFAULT 0,
    "DecisionNote" TEXT,
    "DecidedBy"    NUMERIC(20, 0),
    "DecidedAt"    TIMESTAMP,
    "DateAdded"    TIMESTAMP      NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_GlobalListAppeals_OpenEntry"
    ON "GlobalListAppeals" ("EntryId") WHERE "Status" = 0;
