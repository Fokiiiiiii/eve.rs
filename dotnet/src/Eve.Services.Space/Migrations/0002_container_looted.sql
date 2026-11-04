-- A won container keeps its loot until the loot is taken: 'looted' between the win and the last item leaving it, then 'cleared' (the site
-- objective). The state check gains 'looted'.
ALTER TABLE site_containers DROP CONSTRAINT site_containers_state_check;
ALTER TABLE site_containers ADD CONSTRAINT site_containers_state_check CHECK (state IN ('secure', 'looted', 'cleared', 'destroyed'));
