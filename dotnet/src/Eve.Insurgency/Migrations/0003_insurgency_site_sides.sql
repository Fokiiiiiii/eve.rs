-- The side of each campaign site (slice 4): a pirate site scores the pirates' point of its system when it completes, an empire site the empire side's.
ALTER TABLE insurgency_sites ADD COLUMN side text NOT NULL DEFAULT 'pirate' CHECK (side IN ('pirate', 'antipirate'));
