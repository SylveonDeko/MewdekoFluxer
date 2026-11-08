-- Birthday announcement hour, age message and birth year rule.
-- Migration: 328-AddBirthdayAnnouncementOptions.sql

ALTER TABLE "BirthdayConfigs"
    ADD COLUMN IF NOT EXISTS "AnnouncementHour" INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS "AgeMessage" TEXT,
    ADD COLUMN IF NOT EXISTS "BirthYearRule" INTEGER NOT NULL DEFAULT 1;
