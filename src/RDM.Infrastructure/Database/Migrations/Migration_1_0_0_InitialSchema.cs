namespace RDM.Infrastructure.Database;

public sealed class Migration_1_0_0_InitialSchema : IMigration
{
    public string Version => "1.0.0";
    public string Description => "Initial schema — all tables and indexes";

    public string UpSql => """
        CREATE TABLE IF NOT EXISTS studios (
            studio_id   CHAR(36)        NOT NULL,
            name        VARCHAR(255)    NOT NULL,
            created_at  DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
            PRIMARY KEY (studio_id)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        CREATE TABLE IF NOT EXISTS audio_devices (
            device_id        CHAR(36)        NOT NULL,
            studio_id        CHAR(36)        NOT NULL,
            system_device_id VARCHAR(512)    NOT NULL,
            friendly_name    VARCHAR(255)    NOT NULL,
            driver_type      ENUM('WASAPI_SHARED','WASAPI_EXCLUSIVE','ASIO','DIRECTSOUND') NOT NULL,
            is_available     TINYINT(1)      NOT NULL DEFAULT 0,
            last_seen_at     DATETIME        NULL,
            PRIMARY KEY (device_id),
            CONSTRAINT fk_devices_studio
                FOREIGN KEY (studio_id) REFERENCES studios(studio_id) ON DELETE CASCADE
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        CREATE TABLE IF NOT EXISTS asset_formats (
            format_id   CHAR(36)        NOT NULL,
            name        VARCHAR(64)     NOT NULL,
            description VARCHAR(255)    NULL,
            created_at  DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
            PRIMARY KEY (format_id),
            UNIQUE KEY uq_format_name (name)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        CREATE TABLE IF NOT EXISTS audio_settings (
            settings_id                 CHAR(36)             NOT NULL,
            studio_id                   CHAR(36)             NOT NULL,
            sample_rate                 ENUM('44100','48000','96000') NOT NULL DEFAULT '48000',
            buffer_size                 ENUM('256','512','1024') NOT NULL DEFAULT '512',
            bit_depth                   ENUM('INT16','INT24','FLOAT32') NOT NULL DEFAULT 'FLOAT32',
            output_mode                 ENUM('DIRECTSOUND','WASAPI_SHARED','WASAPI_EXCLUSIVE','ASIO') NOT NULL DEFAULT 'WASAPI_EXCLUSIVE',
            device_player_id            CHAR(36)             NULL,
            device_sweeper_id           CHAR(36)             NULL,
            device_voicetrack_id        CHAR(36)             NULL,
            device_aux1_id              CHAR(36)             NULL,
            device_aux2_id              CHAR(36)             NULL,
            device_aux3_id              CHAR(36)             NULL,
            device_aux4_id              CHAR(36)             NULL,
            device_cartwall_id          CHAR(36)             NULL,
            device_pfl_id               CHAR(36)             NULL,
            device_input_id             CHAR(36)             NULL,
            input_line_fadein_ms        INT UNSIGNED         NOT NULL DEFAULT 200,
            input_line_fadeout_ms       INT UNSIGNED         NOT NULL DEFAULT 1500,
            input_fade_down_music       TINYINT(1)           NOT NULL DEFAULT 1,
            crossfade_enabled           TINYINT(1)           NOT NULL DEFAULT 1,
            crossfade_duration_ms       INT UNSIGNED         NOT NULL DEFAULT 2000,
            crossfade_curve             ENUM('LINEAR','EQUAL_POWER','LOGARITHMIC') NOT NULL DEFAULT 'EQUAL_POWER',
            ducking_enabled             TINYINT(1)           NOT NULL DEFAULT 1,
            ducking_level_db            DECIMAL(4,1)         NOT NULL DEFAULT -12.0,
            ducking_attack_ms           INT UNSIGNED         NOT NULL DEFAULT 200,
            ducking_release_ms          INT UNSIGNED         NOT NULL DEFAULT 500,
            stop_fadeout_ms             INT UNSIGNED         NOT NULL DEFAULT 1250,
            silence_remover_enabled     TINYINT(1)           NOT NULL DEFAULT 0,
            silence_start_threshold_db  DECIMAL(4,1)         NOT NULL DEFAULT -25.0,
            silence_mix_threshold_db    DECIMAL(4,1)         NOT NULL DEFAULT -15.0,
            silence_end_threshold_db    DECIMAL(4,1)         NOT NULL DEFAULT -28.0,
            loudness_target_lufs        DECIMAL(4,1)         NOT NULL DEFAULT -23.0,
            loudness_normalization      TINYINT(1)           NOT NULL DEFAULT 1,
            sweeper_enabled             TINYINT(1)           NOT NULL DEFAULT 1,
            sweeper_format_id           CHAR(36)             NULL,
            sweeper_min_intro_ms        INT UNSIGNED         NOT NULL DEFAULT 5000,
            default_mode                ENUM('AUTO','LIVE_ASSIST','MANUAL') NOT NULL DEFAULT 'LIVE_ASSIST',
            countdown_red_enabled       TINYINT(1)           NOT NULL DEFAULT 1,
            countdown_red_threshold_s   INT UNSIGNED         NOT NULL DEFAULT 30,
            countdown_green_enabled     TINYINT(1)           NOT NULL DEFAULT 1,
            dead_air_enabled            TINYINT(1)           NOT NULL DEFAULT 1,
            dead_air_threshold_s        INT UNSIGNED         NOT NULL DEFAULT 5,
            cartwall_slots_per_page     TINYINT UNSIGNED     NOT NULL DEFAULT 16,
            cartwall_fadeout_ms         INT UNSIGNED         NOT NULL DEFAULT 1000,
            cartwall_separate_window    TINYINT(1)           NOT NULL DEFAULT 0,
            backup_path                 VARCHAR(512)         NULL,
            backup_interval_h           TINYINT UNSIGNED     NOT NULL DEFAULT 24,
            backup_keep_count           TINYINT UNSIGNED     NOT NULL DEFAULT 7,
            backup_on_close             TINYINT(1)           NOT NULL DEFAULT 1,
            backup_last_at              DATETIME             NULL,
            api_enabled                 TINYINT(1)           NOT NULL DEFAULT 1,
            api_port                    SMALLINT UNSIGNED    NOT NULL DEFAULT 9300,
            api_auth_enabled            TINYINT(1)           NOT NULL DEFAULT 1,
            api_username                VARCHAR(64)          NULL,
            api_password_hash           VARCHAR(255)         NULL,
            api_anonymous_local         TINYINT(1)           NOT NULL DEFAULT 0,
            theme                       ENUM('DARK','LIGHT') NOT NULL DEFAULT 'DARK',
            updated_at                  DATETIME             NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
            PRIMARY KEY (settings_id),
            UNIQUE KEY uq_settings_studio (studio_id),
            CONSTRAINT fk_settings_studio
                FOREIGN KEY (studio_id)            REFERENCES studios(studio_id)       ON DELETE CASCADE,
            CONSTRAINT fk_settings_player
                FOREIGN KEY (device_player_id)     REFERENCES audio_devices(device_id) ON DELETE SET NULL,
            CONSTRAINT fk_settings_sweeper
                FOREIGN KEY (device_sweeper_id)    REFERENCES audio_devices(device_id) ON DELETE SET NULL,
            CONSTRAINT fk_settings_voicetrack
                FOREIGN KEY (device_voicetrack_id) REFERENCES audio_devices(device_id) ON DELETE SET NULL,
            CONSTRAINT fk_settings_aux1
                FOREIGN KEY (device_aux1_id)       REFERENCES audio_devices(device_id) ON DELETE SET NULL,
            CONSTRAINT fk_settings_aux2
                FOREIGN KEY (device_aux2_id)       REFERENCES audio_devices(device_id) ON DELETE SET NULL,
            CONSTRAINT fk_settings_aux3
                FOREIGN KEY (device_aux3_id)       REFERENCES audio_devices(device_id) ON DELETE SET NULL,
            CONSTRAINT fk_settings_aux4
                FOREIGN KEY (device_aux4_id)       REFERENCES audio_devices(device_id) ON DELETE SET NULL,
            CONSTRAINT fk_settings_cartwall
                FOREIGN KEY (device_cartwall_id)   REFERENCES audio_devices(device_id) ON DELETE SET NULL,
            CONSTRAINT fk_settings_pfl
                FOREIGN KEY (device_pfl_id)        REFERENCES audio_devices(device_id) ON DELETE SET NULL,
            CONSTRAINT fk_settings_input
                FOREIGN KEY (device_input_id)      REFERENCES audio_devices(device_id) ON DELETE SET NULL,
            CONSTRAINT fk_settings_sweeper_format
                FOREIGN KEY (sweeper_format_id)    REFERENCES asset_formats(format_id) ON DELETE SET NULL
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        CREATE TABLE IF NOT EXISTS assets (
            asset_id        CHAR(36)        NOT NULL,
            asset_type      ENUM('TRACK','CART','SWEEPER','VOICETRACK') NOT NULL DEFAULT 'TRACK',
            format_id       CHAR(36)        NULL,
            title           VARCHAR(255)    NOT NULL,
            artist          VARCHAR(255)    NULL,
            album           VARCHAR(255)    NULL,
            duration_ms     INT UNSIGNED    NOT NULL,
            checksum        CHAR(64)        NOT NULL,
            waveform_cached TINYINT(1)      NOT NULL DEFAULT 0,
            waveform_path   VARCHAR(512)    NULL,
            rdm_file_path   VARCHAR(512)    NULL,
            image_path      VARCHAR(512)    NULL,
            bpm             DECIMAL(5,2)    NULL,
            year            YEAR            NULL,
            rating          TINYINT         NULL CHECK (rating BETWEEN 1 AND 5),
            mood            VARCHAR(64)     NULL,
            gender          VARCHAR(64)     NULL,
            language        VARCHAR(32)     NULL,
            comments        TEXT            NULL,
            is_damaged      TINYINT(1)      NOT NULL DEFAULT 0,
            status          ENUM('PENDING_REVIEW','ACTIVE','DISABLED') NOT NULL DEFAULT 'PENDING_REVIEW',
            start_date      DATETIME        NULL,
            end_date        DATETIME        NULL,
            play_limit      INT UNSIGNED    NULL,
            play_count      INT UNSIGNED    NOT NULL DEFAULT 0,
            last_played_at  DATETIME        NULL,
            loudness_lufs   DECIMAL(5,1)    NULL,
            loudness_peak   DECIMAL(5,1)    NULL,
            created_at      DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
            updated_at      DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
            PRIMARY KEY (asset_id),
            UNIQUE KEY uq_asset_checksum (checksum),
            CONSTRAINT fk_asset_format
                FOREIGN KEY (format_id) REFERENCES asset_formats(format_id) ON DELETE SET NULL
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        CREATE TABLE IF NOT EXISTS asset_cue_points (
            cue_id      CHAR(36)        NOT NULL,
            asset_id    CHAR(36)        NOT NULL,
            marker_type ENUM('INTRO','OUTRO','HOOK','CUSTOM') NOT NULL,
            position_ms INT UNSIGNED    NOT NULL,
            label       VARCHAR(64)     NULL,
            PRIMARY KEY (cue_id),
            CONSTRAINT fk_cue_asset
                FOREIGN KEY (asset_id) REFERENCES assets(asset_id) ON DELETE CASCADE
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        CREATE TABLE IF NOT EXISTS playlists (
            playlist_id CHAR(36)        NOT NULL,
            studio_id   CHAR(36)        NOT NULL,
            name        VARCHAR(255)    NOT NULL,
            created_at  DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
            PRIMARY KEY (playlist_id),
            CONSTRAINT fk_playlists_studio
                FOREIGN KEY (studio_id) REFERENCES studios(studio_id) ON DELETE CASCADE
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        CREATE TABLE IF NOT EXISTS playlist_items (
            item_id             CHAR(36)        NOT NULL,
            playlist_id         CHAR(36)        NOT NULL,
            asset_id            CHAR(36)        NULL,
            position            INT UNSIGNED    NOT NULL,
            item_type           ENUM('ASSET','DUMMY') NOT NULL DEFAULT 'ASSET',
            external_file_path  VARCHAR(512)    NULL,
            dummy_label         VARCHAR(255)    NULL,
            dummy_note          TEXT            NULL,
            dummy_duration_ms   INT UNSIGNED    NULL,
            crossfade_ms        INT UNSIGNED    NULL,
            trim_start_ms       INT UNSIGNED    NULL,
            trim_end_ms         INT UNSIGNED    NULL,
            segue_type          ENUM('AUTO','MANUAL','TIMED') NOT NULL DEFAULT 'AUTO',
            scheduled_at        DATETIME        NULL,
            auto_link_next      TINYINT(1)      NOT NULL DEFAULT 0,
            PRIMARY KEY (item_id),
            UNIQUE KEY uq_item_position (playlist_id, position),
            CONSTRAINT fk_items_playlist
                FOREIGN KEY (playlist_id) REFERENCES playlists(playlist_id) ON DELETE CASCADE,
            CONSTRAINT fk_items_asset
                FOREIGN KEY (asset_id) REFERENCES assets(asset_id) ON DELETE RESTRICT
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        CREATE TABLE IF NOT EXISTS cartwalls (
            cartwall_id CHAR(36)         NOT NULL,
            studio_id   CHAR(36)         NOT NULL,
            name        VARCHAR(255)     NOT NULL,
            page_order  TINYINT UNSIGNED NOT NULL DEFAULT 0,
            hotkey      VARCHAR(32)      NULL,
            PRIMARY KEY (cartwall_id),
            CONSTRAINT fk_cartwalls_studio
                FOREIGN KEY (studio_id) REFERENCES studios(studio_id) ON DELETE CASCADE
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        CREATE TABLE IF NOT EXISTS cart_slots (
            slot_id         CHAR(36)         NOT NULL,
            cartwall_id     CHAR(36)         NOT NULL,
            asset_id        CHAR(36)         NULL,
            slot_number     TINYINT UNSIGNED NOT NULL,
            label           VARCHAR(64)      NULL,
            color           CHAR(7)          NULL,
            hotkey          VARCHAR(32)      NULL,
            `loop`          TINYINT(1)       NOT NULL DEFAULT 0,
            fadeout_ms      INT UNSIGNED     NULL,
            output_gain_db  DECIMAL(4,1)     NOT NULL DEFAULT 0.0,
            PRIMARY KEY (slot_id),
            UNIQUE KEY uq_slot_number (cartwall_id, slot_number),
            CONSTRAINT fk_slots_cartwall
                FOREIGN KEY (cartwall_id) REFERENCES cartwalls(cartwall_id) ON DELETE CASCADE,
            CONSTRAINT fk_slots_asset
                FOREIGN KEY (asset_id) REFERENCES assets(asset_id) ON DELETE SET NULL
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        CREATE TABLE IF NOT EXISTS playback_sessions (
            session_id          CHAR(36)        NOT NULL,
            studio_id           CHAR(36)        NOT NULL,
            current_asset_id    CHAR(36)        NULL,
            current_position_ms INT UNSIGNED    NOT NULL DEFAULT 0,
            next_asset_id       CHAR(36)        NULL,
            playlist_id         CHAR(36)        NULL,
            playlist_item_id    CHAR(36)        NULL,
            state               ENUM('IDLE','PLAYING','PAUSED') NOT NULL DEFAULT 'IDLE',
            mode                ENUM('AUTO','LIVE_ASSIST','MANUAL') NOT NULL DEFAULT 'LIVE_ASSIST',
            snapshot_at         DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
            PRIMARY KEY (session_id),
            UNIQUE KEY uq_session_studio (studio_id),
            CONSTRAINT fk_session_studio
                FOREIGN KEY (studio_id) REFERENCES studios(studio_id) ON DELETE CASCADE
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        CREATE TABLE IF NOT EXISTS playout_log (
            log_id       CHAR(36)        NOT NULL,
            studio_id    CHAR(36)        NOT NULL,
            asset_id     CHAR(36)        NULL,
            temp_title   VARCHAR(255)    NULL,
            temp_artist  VARCHAR(255)    NULL,
            started_at   DATETIME        NOT NULL,
            ended_at     DATETIME        NULL,
            source_type  ENUM('PLAYLIST','CART','SWEEPER','MANUAL') NOT NULL DEFAULT 'PLAYLIST',
            PRIMARY KEY (log_id),
            CONSTRAINT fk_log_studio
                FOREIGN KEY (studio_id) REFERENCES studios(studio_id) ON DELETE CASCADE,
            CONSTRAINT fk_log_asset
                FOREIGN KEY (asset_id) REFERENCES assets(asset_id) ON DELETE RESTRICT
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        CREATE TABLE IF NOT EXISTS scheduled_events (
            event_id        CHAR(36)        NOT NULL,
            studio_id       CHAR(36)        NOT NULL,
            name            VARCHAR(255)    NOT NULL,
            event_type      ENUM('REPEAT','ONE_TIME') NOT NULL DEFAULT 'REPEAT',
            category        VARCHAR(64)     NOT NULL DEFAULT 'Default',
            enabled         TINYINT(1)      NOT NULL DEFAULT 1,
            event_hour      TIME            NULL,
            only_on_date    DATE            NULL,
            days            SET('MON','TUE','WED','THU','FRI','SAT','SUN') NOT NULL DEFAULT 'MON,TUE,WED,THU,FRI,SAT,SUN',
            hours           VARCHAR(255)    NOT NULL DEFAULT '0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23',
            smart_timing    TINYINT(1)      NOT NULL DEFAULT 0,
            actions         JSON            NOT NULL,
            last_fired_at   DATETIME        NULL,
            skip_next       TINYINT(1)      NOT NULL DEFAULT 0,
            created_at      DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
            PRIMARY KEY (event_id),
            CONSTRAINT fk_events_studio
                FOREIGN KEY (studio_id) REFERENCES studios(studio_id) ON DELETE CASCADE
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        CREATE INDEX IF NOT EXISTS idx_assets_type      ON assets (asset_type);
        CREATE INDEX IF NOT EXISTS idx_assets_format    ON assets (format_id);
        CREATE INDEX IF NOT EXISTS idx_assets_title     ON assets (title);
        CREATE INDEX IF NOT EXISTS idx_assets_artist    ON assets (artist);
        CREATE INDEX IF NOT EXISTS idx_assets_damaged   ON assets (is_damaged);
        CREATE INDEX IF NOT EXISTS idx_assets_status    ON assets (status);
        CREATE INDEX IF NOT EXISTS idx_assets_dates     ON assets (start_date, end_date);
        CREATE INDEX IF NOT EXISTS idx_assets_played    ON assets (last_played_at);
        CREATE INDEX IF NOT EXISTS idx_cue_asset        ON asset_cue_points (asset_id, marker_type);
        CREATE INDEX IF NOT EXISTS idx_items_position   ON playlist_items (playlist_id, position);
        CREATE INDEX IF NOT EXISTS idx_items_type       ON playlist_items (playlist_id, item_type);
        CREATE INDEX IF NOT EXISTS idx_cartwalls_studio ON cartwalls (studio_id, page_order);
        CREATE INDEX IF NOT EXISTS idx_log_started      ON playout_log (studio_id, started_at);
        CREATE INDEX IF NOT EXISTS idx_log_asset        ON playout_log (asset_id, started_at);
        CREATE INDEX IF NOT EXISTS idx_devices_studio   ON audio_devices (studio_id, is_available);
        CREATE INDEX IF NOT EXISTS idx_events_studio    ON scheduled_events (studio_id, enabled, event_hour);
        """;
}
