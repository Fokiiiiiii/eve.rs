-- Owner groups (access lists) of the client's ownerGroupManager: the groups, their member entries and the change log.
-- A corporation's built-in group takes the corporation's id as its group id, so it draws no sequence value (the sequence starts above
-- every character, corporation and alliance id).
CREATE SEQUENCE access_group_ids START 2000000001;

CREATE TABLE groups (
    group_id bigint PRIMARY KEY DEFAULT nextval('access_group_ids'),
    creator_id bigint NOT NULL,
    group_name text NOT NULL,
    description text NOT NULL DEFAULT '',
    corp_built_in boolean NOT NULL DEFAULT false,
    created_ms bigint NOT NULL
);
CREATE INDEX groups_creator ON groups(creator_id);

-- One entry per member id in a group. member_kind: 0 everyone (member id 0), 1 character, 2 corporation, 3 alliance.
-- membership_type: -1 none, 0 member, 1 manager, 2 admin, 3 excluded (blocked).
CREATE TABLE members (
    group_id bigint NOT NULL REFERENCES groups(group_id) ON DELETE CASCADE,
    member_id bigint NOT NULL,
    member_kind smallint NOT NULL,
    membership_type smallint NOT NULL,
    PRIMARY KEY (group_id, member_id)
);

-- The change log the client shows (ownerGroupManager.GetGroupLogs). Kept after a group is deleted.
CREATE TABLE logs (
    log_id bigserial PRIMARY KEY,
    group_id bigint NOT NULL,
    character_id bigint NOT NULL,
    action_id smallint NOT NULL,
    owner_id bigint NOT NULL DEFAULT 0,
    old_type smallint,
    new_type smallint,
    old_text text,
    new_text text,
    logged_ms bigint NOT NULL
);
CREATE INDEX logs_group ON logs(group_id, log_id);
