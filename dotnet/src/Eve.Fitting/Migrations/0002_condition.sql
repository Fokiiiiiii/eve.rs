CREATE TABLE ship_condition (
    item_id       bigint           PRIMARY KEY,
    damage        double precision NOT NULL CHECK (damage >= 0 AND damage <= 1),
    charge        double precision NOT NULL CHECK (charge >= 0 AND charge <= 1),
    shield_charge double precision NOT NULL CHECK (shield_charge >= 0 AND shield_charge <= 1),
    armor_damage  double precision NOT NULL CHECK (armor_damage >= 0 AND armor_damage <= 1),
    incapacitated boolean          NOT NULL
);

CREATE TABLE module_condition (
    item_id       bigint           PRIMARY KEY,
    damage        double precision NOT NULL CHECK (damage >= 0 AND damage <= 1),
    shield_charge double precision NOT NULL CHECK (shield_charge >= 0 AND shield_charge <= 1),
    armor_damage  double precision NOT NULL CHECK (armor_damage >= 0 AND armor_damage <= 1),
    incapacitated boolean          NOT NULL
);
