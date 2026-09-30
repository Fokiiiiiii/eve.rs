-- What a ship and its fitted modules have taken, as fractions. A ship with no row has taken nothing (capacitor and shield full); a module with no row
-- was never damaged or mended. A row is kept for as long as the item exists: damage is the item's, not the slot's.
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
