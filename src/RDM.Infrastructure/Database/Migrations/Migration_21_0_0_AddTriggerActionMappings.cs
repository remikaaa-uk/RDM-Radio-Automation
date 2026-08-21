namespace RDM.Infrastructure.Database;

public sealed class Migration_21_0_0_AddTriggerActionMappings : IMigration
{
    public string Version     => "21.0.0";
    public string Description => "Hardware action system — trigger_action_mappings table + seed from hotkeys.json defaults";

    public string UpSql => """
        CREATE TABLE IF NOT EXISTS trigger_action_mappings (
            id                  CHAR(36)        NOT NULL,
            name                VARCHAR(255)    NOT NULL,
            source_device_type  VARCHAR(50)     NOT NULL,
            source_device_id    VARCHAR(255)    NULL,
            target_signature    VARCHAR(255)    NOT NULL,
            target_action_id    VARCHAR(100)    NOT NULL,
            target_parameter    VARCHAR(512)    NULL,
            is_enabled          TINYINT(1)      NOT NULL DEFAULT 1,
            PRIMARY KEY (id),
            INDEX idx_tam_lookup (source_device_type, target_signature)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        INSERT INTO trigger_action_mappings
            (id, name, source_device_type, source_device_id, target_signature, target_action_id, target_parameter, is_enabled)
        VALUES
            (UUID(), 'Space',     'KEYBOARD', NULL, 'Key_Space',        'PlayerPlayStopToggle',  NULL, 1),
            (UUID(), 'Escape',    'KEYBOARD', NULL, 'Key_Escape',       'PlayerStop',            NULL, 1),
            (UUID(), 'Right',     'KEYBOARD', NULL, 'Key_Right',        'PlayerNext',            NULL, 1),
            (UUID(), 'F2',        'KEYBOARD', NULL, 'Key_F2',           'WindowTracksManager',   NULL, 1),
            (UUID(), 'F4',        'KEYBOARD', NULL, 'Key_F4',           'WindowTrackEditor',     NULL, 1),
            (UUID(), 'F5',        'KEYBOARD', NULL, 'Key_F5',           'WindowPlaylistBuilder', NULL, 1),
            (UUID(), 'F6',        'KEYBOARD', NULL, 'Key_F6',           'WindowScheduledEvents', NULL, 1),
            (UUID(), 'F8',        'KEYBOARD', NULL, 'Key_F8',           'CartwallToggleMode',    NULL, 1),
            (UUID(), 'F1',        'KEYBOARD', NULL, 'Key_F1',           'CartwallTab1',          NULL, 1),
            (UUID(), 'F3',        'KEYBOARD', NULL, 'Key_F3',           'CartwallTab2',          NULL, 1),
            (UUID(), 'F7',        'KEYBOARD', NULL, 'Key_F7',           'CartwallTab3',          NULL, 1),
            (UUID(), 'F9',        'KEYBOARD', NULL, 'Key_F9',           'CartwallTab4',          NULL, 1),
            (UUID(), 'F10',       'KEYBOARD', NULL, 'Key_F10',          'CartwallTab5',          NULL, 1),
            (UUID(), 'F11',       'KEYBOARD', NULL, 'Key_F11',          'CartwallTab6',          NULL, 1),
            (UUID(), 'F12',       'KEYBOARD', NULL, 'Key_F12',          'CartwallTab7',          NULL, 1),
            (UUID(), 'D1',        'KEYBOARD', NULL, 'Key_D1',           'CartwallTriggerSlot1',  NULL, 1),
            (UUID(), 'D2',        'KEYBOARD', NULL, 'Key_D2',           'CartwallTriggerSlot2',  NULL, 1),
            (UUID(), 'D3',        'KEYBOARD', NULL, 'Key_D3',           'CartwallTriggerSlot3',  NULL, 1),
            (UUID(), 'D4',        'KEYBOARD', NULL, 'Key_D4',           'CartwallTriggerSlot4',  NULL, 1),
            (UUID(), 'D5',        'KEYBOARD', NULL, 'Key_D5',           'CartwallTriggerSlot5',  NULL, 1),
            (UUID(), 'D6',        'KEYBOARD', NULL, 'Key_D6',           'CartwallTriggerSlot6',  NULL, 1),
            (UUID(), 'D7',        'KEYBOARD', NULL, 'Key_D7',           'CartwallTriggerSlot7',  NULL, 1),
            (UUID(), 'D8',        'KEYBOARD', NULL, 'Key_D8',           'CartwallTriggerSlot8',  NULL, 1),
            (UUID(), 'D9',        'KEYBOARD', NULL, 'Key_D9',           'CartwallTriggerSlot9',  NULL, 1),
            (UUID(), 'Delete',    'KEYBOARD', NULL, 'Key_Delete',       'PlaylistRemoveSelected',NULL, 1),
            (UUID(), 'OemTilde',  'KEYBOARD', NULL, 'Key_OemTilde',     'MicToggle',             NULL, 1),
            (UUID(), 'Ctrl+S',    'KEYBOARD', NULL, 'Key_S_Ctrl',       'Save',                  NULL, 1),
            (UUID(), 'Ctrl+Z',    'KEYBOARD', NULL, 'Key_Z_Ctrl',       'Undo',                  NULL, 1),
            (UUID(), 'Ctrl+Y',    'KEYBOARD', NULL, 'Key_Y_Ctrl',       'Redo',                  NULL, 1);
        """;
}
