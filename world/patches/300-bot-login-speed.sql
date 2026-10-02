-- Bots log in at bot_logins_per_second (default 8) instead of 0.33's fixed 15-minute ramp, which
-- made even a handful of bots take minutes to appear.
UPDATE ServerProperty SET Value = '0' WHERE `Key` = 'startup_ramp_minutes';
