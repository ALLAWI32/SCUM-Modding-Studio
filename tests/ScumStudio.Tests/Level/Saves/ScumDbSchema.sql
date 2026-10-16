-- The schema of SCUM's save database (SCUM.db, PRAGMA user_version 73, journal_mode WAL), read from a single player save
-- the installed game wrote on 2026-10-12 (read 2026-10-16). Schema only: no save data. Used to build synthetic saves for the saved-item cleaner tests.
CREATE TABLE abandoned_bunker
	(
		id                    INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
		user_profile_id       INTEGER,
		map_id                INTEGER NOT NULL,
		location_x            INTEGER NOT NULL,
		location_y            INTEGER NOT NULL,
		is_day                INTEGER NOT NULL, time_since_previous_activation_end REAL NOT NULL DEFAULT 0.0, time_until_activation_start        REAL NOT NULL DEFAULT 0.0, keycard_override_activation_start REAL,

		FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		FOREIGN KEY(map_id)          REFERENCES map(id)          ON DELETE CASCADE
	);
CREATE TABLE abandoned_bunker_alarmed_room
	(
		room_name             TEXT NOT NULL,
		bunker_id             INTEGER NOT NULL,
		alarm_time_remaining  REAL NOT NULL,

		FOREIGN KEY(bunker_id) REFERENCES abandoned_bunker(id) ON DELETE CASCADE
	);
CREATE TABLE abandoned_bunker_bcu_terminal
	(
		room_name             TEXT NOT NULL,
		bunker_id             INTEGER NOT NULL,
		last_download_time    REAL NOT NULL,

		FOREIGN KEY(bunker_id) REFERENCES abandoned_bunker(id) ON DELETE CASCADE
		CONSTRAINT unique_per_room UNIQUE(room_name, bunker_id)
	);
CREATE TABLE abandoned_bunker_mesh_instance_bound_to_activation
	(
		abandoned_bunker_id		INTEGER NOT NULL,
		mesh_instance_name		TEXT NOT NULL,
		examine_time			REAL NOT NULL,

		PRIMARY KEY(abandoned_bunker_id, mesh_instance_name)
		FOREIGN KEY(abandoned_bunker_id) REFERENCES abandoned_bunker(id) ON DELETE CASCADE
	);
CREATE TABLE abandoned_bunker_powered_room
	(
		room_name             TEXT NOT NULL,
		bunker_id             INTEGER NOT NULL,

		FOREIGN KEY(bunker_id) REFERENCES abandoned_bunker(id) ON DELETE CASCADE
	);
CREATE TABLE abandoned_bunker_switchboard_fuse
	(
		room_name             TEXT NOT NULL,
		bunker_id             INTEGER NOT NULL,
		fuse_id               INTEGER NOT NULL,

		FOREIGN KEY(bunker_id) REFERENCES abandoned_bunker(id) ON DELETE CASCADE
		FOREIGN KEY(fuse_id)   REFERENCES entity(id)           ON DELETE CASCADE
	);
CREATE TABLE active_quest
		(
			id               INTEGER PRIMARY KEY NOT NULL,
			user_profile_id  INTEGER,
			map_id           INTEGER NOT NULL,

			quest_data_asset_path  TEXT     NOT NULL,
			auto_complete          BOOLEAN  NOT NULL,
			completion_deadline    REAL     NOT NULL,
			sector                 TEXT     NOT NULL,
			quest_giver_type       INTEGER  NOT NULL,
			rewards_index          INTEGER  NOT NULL,

			FOREIGN KEY(id)              REFERENCES tracking_data_set(id) ON DELETE CASCADE,
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id)      ON DELETE CASCADE,
			FOREIGN KEY(map_id)          REFERENCES map(id)               ON DELETE CASCADE
		);
CREATE TABLE active_task
		(
			id               INTEGER PRIMARY KEY NOT NULL,
			user_profile_id  INTEGER,
			map_id           INTEGER NOT NULL,

			available_task_id  INTEGER NOT NULL,

			FOREIGN KEY(id)                REFERENCES tracking_data_set(id) ON DELETE CASCADE,
			FOREIGN KEY(available_task_id) REFERENCES available_task(id)    ON DELETE CASCADE,
			FOREIGN KEY(user_profile_id)   REFERENCES user_profile(id)      ON DELETE CASCADE,
			FOREIGN KEY(map_id)            REFERENCES map(id)               ON DELETE CASCADE
		);
CREATE TABLE available_task
		(
			id               INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
			user_profile_id  INTEGER,
			map_id           INTEGER NOT NULL,

			task_data_asset_path  TEXT NOT NULL,
			was_ever_completed    BOOLEAN NOT NULL,

			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE,
			FOREIGN KEY(map_id)          REFERENCES map(id)          ON DELETE CASCADE,
			UNIQUE(user_profile_id, map_id, task_data_asset_path)
		);
CREATE TABLE bank_account_registry(
			id										INTEGER NOT NULL,
			map_id									INTEGER,
			user_profile_id							INTEGER,
			account_owner_user_profile_id			INTEGER NOT NULL,
			bank_account_number						INTEGER NOT NULL,
			save_timestamp							INTEGER, used_digital_deluxe_privileges BOOLEAN DEFAULT false,
			PRIMARY KEY(id)
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
			FOREIGN KEY(account_owner_user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE bank_account_registry_cards(
			id									INTEGER NOT NULL,
			map_id								INTEGER,
			user_profile_id						INTEGER,
			bank_account_id						INTEGER NOT NULL,
			card_type							TEXT,
			pin_number							INTEGER,
			wrong_pins_remaining				INTEGER,
			free_renewals_remaining				INTEGER,
			daily_withdraw_amount_remaining		INTEGER,
			daily_deposit_amount_remaining		INTEGER, card_entity_id INTEGER REFERENCES entity(id) ON DELETE SET NULL,
			PRIMARY KEY(id)
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
			FOREIGN KEY(bank_account_id) REFERENCES bank_account_registry(id) ON DELETE CASCADE
		);
CREATE TABLE bank_account_registry_currencies(
			id										INTEGER NOT NULL,
			map_id									INTEGER,
			user_profile_id							INTEGER,
			bank_account_id							INTEGER NOT NULL,
			currency_type							INTEGER NOT NULL,
			account_balance							INTEGER,
			PRIMARY KEY(id)
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
			FOREIGN KEY(bank_account_id) REFERENCES bank_account_registry(id) ON DELETE CASCADE
		);
CREATE TABLE bank_general_data(
			id													INTEGER NOT NULL,
			map_id												INTEGER,
			user_profile_id										INTEGER,
			last_generated_final_set_of_bank_account_numbers	INTEGER DEFAULT 0,
			last_withdrawn_amount_reset_timestamp				INTEGER DEFAULT 0,
			PRIMARY KEY(id)
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE "base" (
			"id"	INTEGER,
			"location_x"	REAL,
			"location_y"	REAL,
			"size_x"	NUMERIC,
			"size_y"	REAL,
			"name"	TEXT,
			"map_id"	INTEGER,
			"user_profile_id"	INTEGER,
			"owner_user_profile_id"	INTEGER,
			"is_owned_by_player"	INTEGER, bounds_min_x REAL, bounds_min_y REAL, bounds_max_x REAL, bounds_max_y REAL,
			PRIMARY KEY("id"),
			FOREIGN KEY("map_id") REFERENCES "map"("id") ON DELETE CASCADE,
			FOREIGN KEY("user_profile_id") REFERENCES "user_profile"("id") ON DELETE CASCADE
		);
CREATE TABLE "base_element" (
			element_id INTEGER,
			base_id INTEGER NOT NULL,
			location_x REAL,
			location_y REAL,
			location_z REAL,
			rotation_pitch REAL,
			rotation_yaw REAL,
			rotation_roll REAL,
			scale_x REAL,
			scale_y REAL,
			scale_z REAL,
			asset TEXT,
			element_health NUMERIC,
			owner_profile_id INTEGER,
			quality REAL DEFAULT 1.0,
			creator_prisoner_id INTEGER,
			PRIMARY KEY(element_id),
			FOREIGN KEY(base_id) REFERENCES base(id) ON DELETE CASCADE,
			FOREIGN KEY(owner_profile_id) REFERENCES user_profile(id) ON DELETE SET NULL
		);
CREATE TABLE base_element_coloring(
		element_id		INTEGER,
		element_part_index	INTEGER NOT NULL,
		element_color_index INTEGER NOT NULL, element_pattern_index INTEGER NOT NULL DEFAULT(255),

		PRIMARY KEY(element_id, element_part_index)
		FOREIGN KEY(element_id) REFERENCES base_element(element_id) ON DELETE CASCADE
	);
CREATE TABLE "base_element_flag" (
			"element_id"	INTEGER,
			"overtake_end_time" INTEGER,
			"overtaker_user_profile_id" INTEGER, expanded_elements INTEGER,
			FOREIGN KEY("element_id") REFERENCES "base_element"("element_id") ON DELETE CASCADE,
			FOREIGN KEY("overtaker_user_profile_id") REFERENCES "user_profile"("id") ON DELETE SET NULL
		);
CREATE TABLE base_element_item
		(
			element_id			INTEGER,
			item_entity_id		INTEGER,

			PRIMARY KEY(element_id, item_entity_id)
			FOREIGN KEY(element_id) REFERENCES base_element(element_id) ON DELETE CASCADE
			FOREIGN KEY(item_entity_id) REFERENCES entity(id) ON DELETE SET NULL
		);
CREATE TABLE "base_element_shelter_map" (
			"element_id"	INTEGER NOT NULL,
			"shelter_id"	INTEGER NOT NULL,
			PRIMARY KEY("element_id","shelter_id"),
			FOREIGN KEY("element_id") REFERENCES "base_element"("element_id") ON DELETE CASCADE,
			FOREIGN KEY("shelter_id") REFERENCES "shelter"("id") ON DELETE CASCADE
		);
CREATE TABLE base_raid_protection(
			id				INTEGER NOT NULL,
			manager_id		INTEGER NOT NULL,
			base_flag_id	INTEGER,
			data			BLOB,

			PRIMARY KEY(id)
			FOREIGN KEY(manager_id) REFERENCES base_raid_protection_manager(id) ON DELETE CASCADE
		);
CREATE TABLE base_raid_protection_manager(
			id					INTEGER NOT NULL,
			map_id				INTEGER NOT NULL,
			user_profile_id		INTEGER,
			protection_type		TINYINT,

			PRIMARY KEY(id)
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE bcu_lock_registry
	(
		map_id							INTEGER NOT NULL,
		user_profile_id					INTEGER,

		server_user_profile_id			INTEGER NOT NULL,
		bcu_lock_entity_id				INTEGER NOT NULL,
		flag_element_id					INTEGER NOT NULL,

		PRIMARY KEY(server_user_profile_id)
		FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
		FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		FOREIGN KEY(server_user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		FOREIGN KEY(bcu_lock_entity_id) REFERENCES entity(id) ON DELETE CASCADE
	);
CREATE TABLE chest_acquisition
	(
		base_id INTEGER NOT NULL,
		entity_id INTEGER NOT NULL,
		acquisition_start_time REAL NOT NULL, owning_user_profile_id INTEGER DEFAULT 0,

		PRIMARY KEY(base_id, entity_id),
		FOREIGN KEY(base_id) REFERENCES base(id) ON DELETE CASCADE,
		FOREIGN KEY(entity_id) REFERENCES entity(id) ON DELETE CASCADE
	);
CREATE TABLE cooking_instance(
			id					INTEGER NOT NULL,
			manager_id			INTEGER NOT NULL,
			utility_type		TEXT,
			bound_utility		INTEGER,
			bound_recipe		INTEGER,
			ingredients			BLOB,
			temperature			REAL,
			softPeakTemperature	REAL,
			progress			REAL,
			cook_quality		INTEGER,
			owner_utility		INTEGER,
			tag_index			INTEGER,
			internal_coords		BLOB,
			packed_locations	BLOB,
			radius				REAL,
			finished			BOOLEAN,
			can_bound_other		BOOLEAN,
			bounded_to			INTEGER,
			cooked_time			REAL, exhaustion_bonus REAL DEFAULT 0.0,

			PRIMARY KEY(id)
			FOREIGN KEY(manager_id) REFERENCES cooking_manager(id) ON DELETE CASCADE
		);
CREATE TABLE cooking_manager(
			id					INTEGER NOT NULL,
			map_id				INTEGER NOT NULL,
			user_profile_id		INTEGER, snapped_keys BLOB, snapped_values BLOB,

			PRIMARY KEY(id)
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
		);
CREATE TABLE custom_zone_configuration(
			id						INTEGER NOT NULL,
			map_id					INTEGER NOT NULL,
			name					CHAR(100),
			color_red				REAL,
			color_green				REAL,
			color_blue				REAL,
			handling_methods		BIGINT, settings INTEGER, user_id TEXT DEFAULT '', num_handling_methods INTEGER NOT NULL DEFAULT 0,
			PRIMARY KEY(id, map_id),
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
		);
CREATE TABLE custom_zone_region(
			id						INTEGER NOT NULL,
			map_id					INTEGER NOT NULL,
			name					CHAR(100),
			location_x				REAL,
			location_y				REAL,
			size_x					REAL,
			size_y					REAL,
			configuration_index		TINYINT, default_region_name CHAR(50), default_region_state TINYINT, user_id TEXT DEFAULT '',
			PRIMARY KEY(id, map_id),
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
		);
CREATE TABLE db_info
		(
			last_vacuum_time 	TEXT,
			next_free_id		INTEGER DEFAULT 1
		);
CREATE TABLE "door_locking_registry_data" (
			asset	TEXT NOT NULL,
			count	INTEGER NOT NULL,
			type	INTEGER NOT NULL,
			map_id		INTEGER NOT NULL,
			user_profile_id	INTEGER,
			PRIMARY KEY(user_profile_id, map_id, type, asset),
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE,
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE economy(
			id						INTEGER NOT NULL,
			map_id					INTEGER,
			user_profile_id			INTEGER,
			save_timestamp			INTEGER, time_since_last_economy_reset INTEGER NOT NULL DEFAULT 0,
			PRIMARY KEY(id)
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE economy_outpost_gold(
			id												INTEGER NOT NULL,
			map_id											INTEGER,
			user_profile_id									INTEGER,
			outpost_id										INTEGER NOT NULL,
			gold_buying_capability_funds					INTEGER,
			gold_selling_capability_funds					INTEGER,
			gold_selling_capability_funds_restock_amount	REAL,
			PRIMARY KEY(id)
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
			FOREIGN KEY(outpost_id) REFERENCES economy_outposts(id) ON DELETE CASCADE
		);
CREATE TABLE economy_outposts(
			id									INTEGER NOT NULL,
			map_id								INTEGER,
			user_profile_id						INTEGER,
			outpost_runtime_id					TEXT,
			buying_capability					REAL,
			outpost_bank_funds					REAL,
			economy_reset_elapsed_time			REAL,
			prices_randomization_elapsed_time	REAL,
			save_timestamp						INTEGER, price_delta_seed REAL, tradeable_rotation_elapsed_time REAL,
			PRIMARY KEY(id)
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE economy_special_deals
		(
			id               INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
			map_id           INTEGER NOT NULL,
			user_profile_id  INTEGER NOT NULL,

			sector TEXT NOT NULL,

			tradeable_asset TEXT NOT NULL,

			base_purchase_price        INTEGER,
			amount_in_store            INTEGER,
			override_purchase_ability  BOOLEAN,
			can_be_purchased_by_player BOOLEAN,
			required_fame_points       INTEGER, trader TEXT NOT NULL,

			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE,
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE,

			UNIQUE(user_profile_id, map_id, sector, tradeable_asset)
		);
CREATE TABLE economy_tradeables_info(
			id						INTEGER NOT NULL,
			map_id					INTEGER,
			user_profile_id			INTEGER,
			trader_id				INTEGER NOT NULL,
			tradeable_asset			TEXT,
			amount_in_store			INTEGER NOT NULL,
			restock_amount			REAL, is_omitted_from_current_rotation BOOLEAN, partial_amount_in_store REAL DEFAULT 0.0,
			PRIMARY KEY(id)
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
			FOREIGN KEY(trader_id) REFERENCES economy_traders(id) ON DELETE CASCADE
		);
CREATE TABLE economy_traders(
			id						INTEGER NOT NULL,
			map_id					INTEGER,
			user_profile_id			INTEGER,
			trader_runtime_id		TEXT,
			available_funds			INTEGER,
			PRIMARY KEY(id)
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE "elevated_users"(
			user_id		TEXT NOT NULL,
			PRIMARY KEY(user_id)
		);
CREATE TABLE entity
		(
			id 					INTEGER PRIMARY KEY NOT NULL,
			entity_system_id	INTEGER NOT NULL,
			class				TEXT NOT NULL,
			owning_entity_id	INTEGER,
			parent_entity_id	INTEGER,
			location_x			REAL DEFAULT 0.0 NOT NULL,
			location_y			REAL DEFAULT 0.0 NOT NULL,
			location_z			REAL DEFAULT 0.0 NOT NULL,
			rotation_x			REAL DEFAULT 0.0 NOT NULL,
			rotation_y			REAL DEFAULT 0.0 NOT NULL,
			rotation_z			REAL DEFAULT 0.0 NOT NULL,
			scale_x				REAL DEFAULT 1.0 NOT NULL,
			scale_y				REAL DEFAULT 1.0 NOT NULL,
			scale_z				REAL DEFAULT 1.0 NOT NULL,
			flags				INTEGER DEFAULT 0 NOT NULL,
			data 				BLOB, reason TEXT,

			FOREIGN KEY(entity_system_id) REFERENCES entity_system(id) ON DELETE CASCADE
			FOREIGN KEY(owning_entity_id) REFERENCES entity(id) ON DELETE CASCADE
			FOREIGN KEY(parent_entity_id) REFERENCES entity(id) ON DELETE SET NULL
		);
CREATE TABLE entity_component(
			id				INTEGER NOT NULL,
			entity_id		INTEGER NOT NULL,
			name			TEXT NOT NULL,
			class			TEXT NOT NULL,
			flags			INTEGER NOT NULL DEFAULT 0,
			data			BLOB,

			PRIMARY KEY(id)
			FOREIGN KEY(entity_id) REFERENCES entity(id) ON DELETE CASCADE
		);
CREATE TABLE entity_system
	(
		id						INTEGER NOT NULL,
		map_id					INTEGER NOT NULL,
		user_profile_id			INTEGER,
		timestamp 				REAL NOT NULL DEFAULT 0.0,

		PRIMARY KEY(id)
		FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
		FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
	);
CREATE TABLE entity_to_delete_on_startup
		(
			entity_id 			INTEGER NOT NULL,

			PRIMARY KEY(entity_id)
			FOREIGN KEY(entity_id) REFERENCES entity(id) ON DELETE CASCADE
		);
CREATE TABLE event(
			id								INTEGER NOT NULL,
			PRIMARY KEY(id)
		);
CREATE TABLE "event_participant"(
			event_id						INTEGER NOT NULL,
			user_profile_id					INTEGER NOT NULL,
			PRIMARY KEY(user_profile_id, event_id)
			FOREIGN KEY(event_id) REFERENCES event(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE event_rankings_cached
		(
			user_id					TEXT NOT NULL,
			user_profile_id			INTEGER NOT NULL,
			name					TEXT NOT NULL,
			is_banned				INTEGER NOT NULL DEFAULT 0,
			fame_points				REAL NOT NULL DEFAULT 0.0,
			score					REAL NOT NULL DEFAULT 0.0,
			enemy_kills				INTEGER NOT NULL DEFAULT 0,
			team_kills				INTEGER NOT NULL DEFAULT 0,
			deaths					INTEGER NOT NULL DEFAULT 0,
			suicides				INTEGER NOT NULL DEFAULT 0,
			assists					INTEGER NOT NULL DEFAULT 0,
			headshots				INTEGER NOT NULL DEFAULT 0,
			ctf_pickups				INTEGER NOT NULL DEFAULT 0,
			ctf_captures			INTEGER NOT NULL DEFAULT 0,
			ctf_returns				INTEGER NOT NULL DEFAULT 0,
			dm_longest_headshot		REAL NOT NULL DEFAULT 0.0,
			dm_melee_kills			INTEGER NOT NULL DEFAULT 0,
			dm_longest_life			REAL NOT NULL DEFAULT 0.0,
			dz_activations			INTEGER NOT NULL DEFAULT 0,

			PRIMARY KEY(user_profile_id),
			FOREIGN KEY(user_id) REFERENCES user(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE event_round(
			id								INTEGER NOT NULL,
			event_id						INTEGER NOT NULL,
			start_time						TEXT,
			end_time						TEXT,
			PRIMARY KEY(id)
			FOREIGN KEY(event_id) REFERENCES event(id) ON DELETE CASCADE
		);
CREATE TABLE "event_round_stats"(
			round_id						INTEGER NOT NULL,
			user_profile_id					INTEGER NOT NULL,
			score							REAL,
			enemy_kills						INTEGER,
			team_kills						INTEGER,
			deaths							INTEGER,
			suicides						INTEGER,
			assists							INTEGER,
			headshots						INTEGER,
			PRIMARY KEY(user_profile_id, round_id)
			FOREIGN KEY(round_id) REFERENCES event_round(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE "event_round_stats_ctf"(
			round_id						INTEGER NOT NULL,
			user_profile_id					INTEGER NOT NULL,
			pickups							INTEGER,
			captures						INTEGER,
			returns							INTEGER,
			PRIMARY KEY(user_profile_id, round_id)
			FOREIGN KEY(round_id) REFERENCES event_round(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE "event_round_stats_dm"(
			round_id						INTEGER NOT NULL,
			user_profile_id					INTEGER NOT NULL,
			longest_headshot				REAL,
			melee_kills						INTEGER,
			longest_life					REAL,
			PRIMARY KEY(user_profile_id, round_id)
			FOREIGN KEY(round_id) REFERENCES event_round(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE "event_round_stats_dz"(
			round_id						INTEGER NOT NULL,
			user_profile_id					INTEGER NOT NULL,
			activations						INTEGER,
			PRIMARY KEY(user_profile_id, round_id)
			FOREIGN KEY(round_id) REFERENCES event_round(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE events_stats
		(
			user_profile_id					INTEGER NOT NULL,
			events_won						INTEGER DEFAULT 0,
			events_lost						INTEGER DEFAULT 0,
			enemy_kills						INTEGER DEFAULT 0,
			deaths							INTEGER DEFAULT 0,
			ctf_captures					INTEGER DEFAULT 0,
			team_kills						INTEGER DEFAULT 0,
			PRIMARY KEY(user_profile_id)
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE expirable_entity_component
	(
		entity_component_id     INTEGER NOT NULL,
		start_play_time REAL, duration REAL,

		PRIMARY KEY(entity_component_id)
		FOREIGN KEY(entity_component_id) REFERENCES entity_component(id) ON DELETE CASCADE
	);
CREATE TABLE "expirable_vehicle_spawner_info"(
			map_id					INTEGER,
			user_profile_id			INTEGER,
			spawner_name			TEXT,
			num_spawners_created	INTEGER,
			PRIMARY KEY(user_profile_id, map_id, spawner_name)
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE "finished_timed_gift_spawner"(
			map_id								INTEGER,
			user_profile_id						INTEGER,
			spawn_time							INTEGER,
			PRIMARY KEY(user_profile_id, map_id, spawn_time)
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE fishing_stats
		(
			user_profile_id					INTEGER NOT NULL,
			fish_caught						INTEGER DEFAULT 0,
			fish_kept						INTEGER DEFAULT 0,
			fish_released					INTEGER DEFAULT 0,
			lines_broken					INTEGER DEFAULT 0,
			heaviest_fish_caught			   REAL DEFAULT 0.0,
			longest_fish_caught				   REAL DEFAULT 0.0,
			bass_caught						INTEGER DEFAULT 0,
			catfish_caught					INTEGER DEFAULT 0,
			pike_caught						INTEGER DEFAULT 0,
			carp_caught						INTEGER DEFAULT 0,
			amur_caught						INTEGER DEFAULT 0,
			bleak_caught					INTEGER DEFAULT 0,
			chub_caught						INTEGER DEFAULT 0,
			ruffe_caught					INTEGER DEFAULT 0,
			prussian_carp_caught			INTEGER DEFAULT 0,
			crucian_carp_caught				INTEGER DEFAULT 0,
			sardine_caught					INTEGER DEFAULT 0,
			dentex_caught					INTEGER DEFAULT 0,
			orata_caught					INTEGER DEFAULT 0,
			tuna_caught						INTEGER DEFAULT 0,
			PRIMARY KEY(user_profile_id)
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE garden(
			id							INTEGER NOT NULL,
			garden_manager_id			INTEGER NOT NULL,
			owner_user_profile_id		INTEGER,
			owner_skill_level			INTEGER,
			num_slots_x					INTEGER,
			num_slots_y					INTEGER,
			location_x					REAL,
			location_y					REAL,
			location_z					REAL,
			rotation_r					REAL,
			rotation_p					REAL,
			rotation_y					REAL,
			crafted_age					REAL,
			has_greenhouse				BOOL,
			pest_disease_check_timer	REAL, health REAL NOT NULL DEFAULT 0.0,
			PRIMARY KEY(id)
			FOREIGN KEY(garden_manager_id) REFERENCES garden_manager(id) ON DELETE CASCADE
			FOREIGN KEY(owner_user_profile_id) REFERENCES user_profile(id) ON DELETE SET NULL
		);
CREATE TABLE garden_manager(
			id							INTEGER NOT NULL,
			user_profile_id				INTEGER,
			map_id						INTEGER NOT NULL,
			sim_data_sun_irradiance		REAL,
			sim_data_rain_average		REAL,
			sim_data_time_range			REAL,
			PRIMARY KEY(id)
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
		);
CREATE TABLE garden_manager_height_relative_sim_data(
			id							INTEGER NOT NULL,
			garden_manager_id			INTEGER NOT NULL,
			height						REAL,
			temperature					REAL,
			water_evaporation			REAL,
			PRIMARY KEY(id)
			FOREIGN KEY(garden_manager_id) REFERENCES garden_manager(id) ON DELETE CASCADE
		);
CREATE TABLE garden_slot(
			id							INTEGER NOT NULL,
			garden_id					INTEGER NOT NULL,
			planter_user_profile_id		INTEGER,
			planter_skill_level			INTEGER,
			slot_position_x				INTEGER,
			slot_position_y				INTEGER,
			species						TEXT,
			stage						INTEGER,
			growth_percentage			REAL,
			water						REAL,
			organic_fertilizer			BOOL,
			industrial_fertilizer		BOOL,
			weed_intenstiy				REAL,
			pesticide_timer				REAL,
			fungicide_timer				REAL,
			weed_repellent_timer		REAL,
			health						REAL,
			dehidration_death_timer		REAL,
			freezing_death_timer		REAL,
			pest_death_timer			REAL,
			disease_death_timer			REAL,
			old_age_death_timer			REAL,
			weed_check_timer			REAL,
			dead						BOOL,

			PRIMARY KEY(id)
			FOREIGN KEY(garden_id) REFERENCES garden(id) ON DELETE CASCADE
			FOREIGN KEY(planter_user_profile_id) REFERENCES user_profile(id) ON DELETE SET NULL
		);
CREATE TABLE garden_slot_pest(
			id							INTEGER NOT NULL,
			garden_slot_id				INTEGER NOT NULL,
			pest_species				TEXT,
			pest_intensity				REAL,
			disease_species				TEXT,
			disease_intensity			REAL,
			PRIMARY KEY(id)
			FOREIGN KEY(garden_slot_id) REFERENCES garden_slot(id) ON DELETE CASCADE
		);
CREATE TABLE global_encounter_manager
		(
			id								INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
			map_id							INTEGER NOT NULL,
			user_profile_id					INTEGER, max_group_id INTEGER DEFAULT 0,

			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE,
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
		);
CREATE TABLE global_encounter_manager_bb_data
		(
			id										INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
			global_encounter_manager_id				INTEGER NOT NULL,
			base_id									INTEGER NOT NULL,

			at_least_one_base_member_present_time	REAL,
			encounter_cooldown						REAL, base_lifetime REAL DEFAULT 0.0,

			FOREIGN KEY(global_encounter_manager_id) REFERENCES global_encounter_manager(id) ON DELETE CASCADE
		);
CREATE TABLE global_encounter_manager_bb_data_flags
		(
			id										INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
			global_encounter_manager_id				INTEGER NOT NULL,
			global_encounter_manager_bb_data_id		INTEGER NOT NULL,
			flag_id									INTEGER NOT NULL,

			FOREIGN KEY(global_encounter_manager_id) REFERENCES global_encounter_manager(id) ON DELETE CASCADE
			FOREIGN KEY(global_encounter_manager_bb_data_id) REFERENCES global_encounter_manager_bb_data(id) ON DELETE CASCADE
		);
CREATE TABLE global_encounter_manager_rogue_characters (
			id								INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
			global_encounter_manager_id		INTEGER NOT NULL,

			character_class					STRING,
			character_location_x			REAL,
			character_location_y			REAL,
			character_location_z			REAL,
			character_rotation_yaw			REAL,
			character_data					BLOB, preset_type TEXT, despawn_lifetime REAL DEFAULT 0.0, despawn_lifetime_remaining REAL DEFAULT 0.0,

			FOREIGN KEY(global_encounter_manager_id) REFERENCES global_encounter_manager(id) ON DELETE CASCADE
		);
CREATE TABLE global_encounter_manager_visited_points (
			id								INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
			global_encounter_manager_id		INTEGER NOT NULL,

			location_x						REAL,
			location_y						REAL,
			location_z						REAL,
			xy_size							REAL,
			z_size							REAL,
			stay_alive_timestamp			REAL,
			remove_time						REAL,
			is_subzone_point				BOOLEAN,

			FOREIGN KEY(global_encounter_manager_id) REFERENCES global_encounter_manager(id) ON DELETE CASCADE
		);
CREATE TABLE global_encounter_manager_zone_cooldown
		(
			id								INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
			global_encounter_manager_id		INTEGER NOT NULL,

			zone_location_x					REAL,
			zone_location_y					REAL,
			zone_location_z					REAL,
			sub_zone_index					INTEGER,
			cooldown						REAL,

			FOREIGN KEY(global_encounter_manager_id) REFERENCES global_encounter_manager(id) ON DELETE CASCADE
		);
CREATE TABLE "global_radiation_data"(
			map_id 				INTEGER NOT NULL,
			user_profile_id		INTEGER,
			timestamp			INTEGER DEFAULT 0 NOT NULL,
			PRIMARY KEY(user_profile_id, map_id)
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE gold_price_master_multipliers(
			id					INTEGER NOT NULL,
			date				INTEGER,
			multiplier			REAL,
			PRIMARY KEY(id)
		);
CREATE TABLE heat_source(
			id						INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
			map_id					INTEGER,
			user_profile_id			INTEGER,
			location_x				REAL,
			location_y				REAL,
			location_z				REAL,
			temperature				REAL,
			burning_speed			REAL,
			distance_scale			REAL,
			inner_radius			REAL,
			outer_radius			REAL,
			fuel_capacity REAL, temperature_curve_path TEXT, fuel BLOB, uses_fuel_simulation BOOL NOT NULL DEFAULT 0, operating_temperature REAL,
			FOREIGN KEY(map_id) 			REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) 	REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE "item_container_upgrades" (
			"id"					INTEGER NOT NULL,
			"item_container_id"		INTEGER NOT NULL,
			"asset"					TEXT,
			"num_tries"				INTEGER,
			PRIMARY KEY("id")
			FOREIGN KEY("item_container_id") REFERENCES item_containers(id) ON DELETE CASCADE
		);
CREATE TABLE "item_containers" (
			"id"					INTEGER NOT NULL,
			"map_id"				INTEGER NOT NULL,
			"user_profile_id"		INTEGER,
			"runtime_id"			TEXT NOT NULL,
			"lock_reset_time"		INTEGER,
			PRIMARY KEY("id")
			FOREIGN KEY("map_id") REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY("user_profile_id") REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE item_entity
		(
			entity_id 			INTEGER NOT NULL,
			health				REAL,
			max_health			REAL,
			radiation_amount	REAL,
			xml					TEXT, weight REAL, water_weight REAL, external_weight REAL, total_weight REAL, max_uses INTEGER, flags INTEGER, body_damage_data BLOB,

			PRIMARY KEY(entity_id)
			FOREIGN KEY(entity_id) REFERENCES entity(id) ON DELETE CASCADE
		);
CREATE TABLE item_entity_spawner
	(
		id						INTEGER NOT NULL,
		name					TEXT NOT NULL,
		entity_system_id		INTEGER NOT NULL,
		evaluation_time			REAL NOT NULL DEFAULT 0.0,
		num_evaluations			INTEGER DEFAULT 0,

		PRIMARY KEY(id)
		FOREIGN KEY(entity_system_id) REFERENCES entity_system(id) ON DELETE CASCADE
	);
CREATE TABLE item_entity_spawner_cooldown
	(
		item_entity_spawner_id	INTEGER NOT NULL,
		key						INTEGER NOT NULL,
		value					REAL NOT NULL DEFAULT 0.0,

		PRIMARY KEY(item_entity_spawner_id, key)
		FOREIGN KEY(item_entity_spawner_id) REFERENCES item_entity_spawner(id) ON DELETE CASCADE
	);
CREATE TABLE item_entity_spawner_entry
	(
		item_entity_spawner_id	INTEGER NOT NULL,
		idx						INTEGER NOT NULL,
		entity_id				INTEGER,

		PRIMARY KEY(item_entity_spawner_id, idx)
		FOREIGN KEY(item_entity_spawner_id) REFERENCES item_entity_spawner(id) ON DELETE CASCADE
		FOREIGN KEY(entity_id) REFERENCES item_entity(entity_id) ON DELETE SET NULL
	);
CREATE TABLE "item_radiation_exposure_data"(
			item_id_1 					INTEGER NOT NULL,
			item_id_2					INTEGER NOT NULL,
			map_id 						INTEGER NOT NULL,
			user_profile_id				INTEGER,
			radiation_amount 			REAL DEFAULT 0.0 NOT NULL,
			pending_radiation_amount 	REAL DEFAULT 0.0 NOT NULL,
			modified_timestamp			INTEGER DEFAULT 0 NOT NULL,
			PRIMARY KEY(user_profile_id, map_id, item_id_1, item_id_2)
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE item_spawner_evaluation_result_entity_component
	(
		entity_component_id		INTEGER NOT NULL,
		usage_ratio				REAL NOT NULL,
		health_ratio			REAL NOT NULL, stack_ratio REAL NOT NULL DEFAULT 0.0,

		PRIMARY KEY(entity_component_id)
		FOREIGN KEY(entity_component_id) REFERENCES entity_component(id) ON DELETE CASCADE
	);
CREATE TABLE item_spawner_evaluation_result_entity_component_post_spawn_action
	(
		entity_component_id		INTEGER NOT NULL,
		idx						INTEGER NOT NULL,
		action					TEXT NOT NULL,

		PRIMARY KEY(entity_component_id, idx)
		FOREIGN KEY(entity_component_id) REFERENCES entity_component(id) ON DELETE CASCADE
	);
CREATE TABLE item_spawning_cooldown_groups
		(
			entity_system_id		INTEGER NOT NULL,
			idx						INTEGER NOT NULL,
			cooldown_min			INTEGER NOT NULL DEFAULT 0,
			cooldown_max			INTEGER NOT NULL DEFAULT 0,

			FOREIGN KEY(entity_system_id) REFERENCES entity_system(id) ON DELETE CASCADE
		);
CREATE TABLE key_card_item_entity
	(
		entity_id           INTEGER NOT NULL,
		spawn_reason        TEXT,

		PRIMARY KEY(entity_id)
		FOREIGN KEY(entity_id) REFERENCES entity(id) ON DELETE CASCADE
	);
CREATE TABLE "killbox" (
			name						TEXT NOT NULL,
			map_id					INTEGER NOT NULL,
			user_profile_id			INTEGER,
			is_active					BOOL,
			is_finale					BOOL,
			remaining_time			REAL, rooms_difficulty_packed INTEGER, rooms_state_packed INTEGER,
			PRIMARY KEY(user_profile_id, map_id, name)
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE local_chat_history(
			chat_line			TEXT NOT NULL
		);
CREATE TABLE map(
	id		INTEGER NOT NULL,
	name	TEXT,
	PRIMARY KEY(id)
);
CREATE TABLE muted_users(
			user_id_hashed			TEXT,
			user_profile_name		TEXT,
			PRIMARY KEY(user_id_hashed)
		);
CREATE TABLE "notification" (
			id	INTEGER NOT NULL,
			user_profile_id	INTEGER NOT NULL,
			data	BLOB,
			timestamp	INTEGER,
			PRIMARY KEY(id),
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE penalty_squad_leave_info
		(
			user_id TEXT,
			squadmates_left INTEGER NOT NULL,

			PRIMARY KEY(user_id),
			FOREIGN KEY(user_id) REFERENCES user(id) ON DELETE CASCADE
		);
CREATE TABLE placeable(
			id						INTEGER NOT NULL,
			prisoner_id				INTEGER NOT NULL,
			map_id					INTEGER NOT NULL,
			asset					TEXT,

			location_x				REAL,
			location_y				REAL,
			location_z				REAL,

			rotation_pitch			REAL,
			rotation_yaw			REAL,
			rotation_roll			REAL,

			placement_location_x 	REAL,
			placement_location_y 	REAL,
			placement_location_z 	REAL, process BLOB,

			PRIMARY KEY(id)
			FOREIGN KEY(prisoner_id) REFERENCES prisoner(id) ON DELETE CASCADE
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
		);
CREATE TABLE placeable_basebuilding (
			"placeable_id"	INTEGER NOT NULL,
			"first_point_location_x"	REAL,
			"first_point_location_y"	REAL,
			"first_point_location_z"	REAL,
			"state_flags"				INTEGER, first_point_rotation_pitch REAL, first_point_rotation_yaw REAL, first_point_rotation_roll REAL,
			FOREIGN KEY("placeable_id") REFERENCES "placeable"("id") ON DELETE CASCADE
		);
CREATE TABLE placeable_garden
		(
			id 									INTEGER NOT NULL,
			placeable_id						INTEGER NOT NULL,
			x_min								INTEGER,
			y_min								INTEGER,
			x_max								INTEGER,
			y_max								INTEGER,

			PRIMARY KEY(id)
			FOREIGN KEY(placeable_id) REFERENCES placeable(id) ON DELETE CASCADE
		);
CREATE TABLE "placeable_upgrade" (
			"placeable_id"	INTEGER NOT NULL,
			"element_base_id"	INTEGER NOT NULL,
			"element_id"	INTEGER NOT NULL,
			"element_location_x"	REAL,
			"element_location_y"	REAL,
			"element_location_z"	REAL,
			FOREIGN KEY("placeable_id") REFERENCES "placeable"("id") ON DELETE CASCADE
		);
CREATE TABLE prisoner(
	id								INTEGER NOT NULL,
	user_profile_id					INTEGER,
	is_alive						INTEGER,
	time_of_death					TEXT,
	team_index						INTEGER, -- Should this be in a profile?
	appearance_index						INTEGER,
	gender				INTEGER,
	head_tattoo_index				INTEGER,
	body_tattoo_index				INTEGER,
	stance							INTEGER,
	melee_target_selection_mode		INTEGER,
	head_water_weight				REAL,
	upper_body_water_weight			REAL,
	lower_body_water_weight			REAL,
	feet_water_weight				REAL,
	penis_size REAL, breast_size INTEGER, time_of_revive TEXT, body_simulation BLOB, should_play_intro_cinematic BOOLEAN, has_open_parachute BOOLEAN, eligible_for_free_plastic_surgery BOOLEAN DEFAULT false, last_character_dlcs_owned BOOLEAN DEFAULT false, age TINYINT DEFAULT 20, appearance_hair_style_index TINYINT DEFAULT 0, appearance_face_type_index TINYINT DEFAULT 0, appearance_skin_tone_index TINYINT DEFAULT 0, appearance_hair_color_index TINYINT DEFAULT 0, appearance_eye_color_index TINYINT DEFAULT 0, appearance_iris_type_index TINYINT DEFAULT 0, appearance_eye_makeup_metalness TINYINT DEFAULT 0, appearance_eye_makeup_intensity TINYINT DEFAULT 0, appearance_lipstick_roughness TINYINT DEFAULT 0, appearance_lipstick_intensity TINYINT DEFAULT 0, appearance_eyeshadow_color_r TINYINT DEFAULT 0, appearance_eyeshadow_color_g TINYINT DEFAULT 0, appearance_eyeshadow_color_b TINYINT DEFAULT 0, appearance_eyeliner_color_r TINYINT DEFAULT 0, appearance_eyeliner_color_g TINYINT DEFAULT 0, appearance_eyeliner_color_b TINYINT DEFAULT 0, appearance_lipstick_color_r TINYINT DEFAULT 0, appearance_lipstick_color_g TINYINT DEFAULT 0, appearance_lipstick_color_b TINYINT DEFAULT 0, awarded_character_dlc_plastic_surgeries BOOLEAN DEFAULT false, should_spawn_default_equipment INTEGER, last_save_time INTEGER, is_in_bed BOOLEAN, appearance_moustache_style_index INTEGER DEFAULT -1, appearance_beard_style_index INTEGER DEFAULT -1, appearance_facial_hair_color_index INTEGER DEFAULT 0, appearance_has_body_hair BOOLEAN DEFAULT false, appearance_tattoo_indexes BLOB,
	PRIMARY KEY(id)
	FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
);
CREATE TABLE prisoner_bondage (
			prisoner_id INTEGER NOT NULL,
			body_part INTEGER NOT NULL,
			tightness REAL, item_entity_id INTEGER NOT NULL REFERENCES entity(id) ON DELETE CASCADE,
			PRIMARY KEY(prisoner_id, body_part)
			FOREIGN KEY(prisoner_id) REFERENCES prisoner(id) ON DELETE CASCADE
		);
CREATE TABLE prisoner_consumed_items
		(
			prisoner_id 						INTEGER NOT NULL,
			item_name							TEXT NOT NULL,
			last_consumed_time					INTEGER,
			consumed_amount						REAL,

			PRIMARY KEY(prisoner_id, item_name)
			FOREIGN KEY(prisoner_id) REFERENCES prisoner(id) ON DELETE CASCADE
		);
CREATE TABLE prisoner_entity
		(
			entity_id 			INTEGER NOT NULL,
			prisoner_id			INTEGER,

			PRIMARY KEY(entity_id)
			FOREIGN KEY(entity_id) REFERENCES entity(id) ON DELETE CASCADE
			FOREIGN KEY(prisoner_id) REFERENCES prisoner(id) ON DELETE CASCADE
		);
CREATE TABLE prisoner_inventory_quick_access_slot
		(
			prisoner_entity_id		INTEGER NOT NULL,
			slot_index				INTEGER NOT NULL,
			item_entity_setup				TEXT NOT NULL,
			item_entity_id			INTEGER,
			is_in_throwing_mode		INTEGER DEFAULT 0 NOT NULL,

			PRIMARY KEY(prisoner_entity_id, slot_index)
			FOREIGN KEY(prisoner_entity_id) REFERENCES entity(id) ON DELETE CASCADE
			FOREIGN KEY(item_entity_id) REFERENCES entity(id) ON DELETE SET NULL
		);
CREATE TABLE prisoner_prison_wallet
		(
			prisoner_entity_id			INTEGER NOT NULL,
			is_deluxe_item_pending		INTEGER NOT NULL,

			PRIMARY KEY(prisoner_entity_id)
			FOREIGN KEY(prisoner_entity_id) REFERENCES entity(id) ON DELETE CASCADE
		);
CREATE TABLE prisoner_respawn_info(
			prisoner_id				INTEGER,
			penalty_random			INTEGER,
			penalty_sector			INTEGER,
			penalty_shelter			INTEGER,
			penalty_squad			INTEGER,
			last_use_random			TEXT,
			last_use_sector			TEXT,
			last_use_shelter		TEXT,
			last_use_squad			TEXT, commit_suicide_penalty INTEGER, commit_suicide_last_use TEXT, commit_suicide_cooldown_left FLOAT,
			PRIMARY KEY(prisoner_id)
			FOREIGN KEY(prisoner_id) REFERENCES prisoner(id) ON DELETE CASCADE
		);
CREATE TABLE prisoner_skill(
	prisoner_id						INTEGER NOT NULL,
	name							TEXT,
	level							INTEGER,
	experience						REAL,
	xml								TEXT,
	FOREIGN KEY(prisoner_id) REFERENCES prisoner(id) ON DELETE CASCADE
);
CREATE TABLE prisoner_spawn_location(
			prisoner_id			INTEGER NOT NULL,
			map_id				INTEGER NOT NULL,
			type				INTEGER NOT NULL,
			shelter_id			INTEGER,
			location_x			REAL,
			location_y			REAL,
			location_z			REAL,
			rotation_pitch		REAL,
			rotation_yaw		REAL,
			rotation_roll		REAL, velocity_x REAL, velocity_y REAL, velocity_z REAL,
			PRIMARY KEY(prisoner_id, map_id, type)
			FOREIGN KEY(prisoner_id) REFERENCES prisoner(id) ON DELETE CASCADE
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(shelter_id) REFERENCES shelter(id) ON DELETE CASCADE
		);
CREATE TABLE prisoner_stats_tracking_info
		(
			prisoner_id							INTEGER NOT NULL,
			has_ever_maxed_out_core_attributes	INTEGER DEFAULT 0,

			PRIMARY KEY(prisoner_id)
			FOREIGN KEY(prisoner_id) REFERENCES prisoner(id) ON DELETE CASCADE
		);
CREATE TABLE prisoner_vehicle_mountee_info
		(
			prisoner_id			INTEGER NOT NULL,
			vehicle_entity_id	INTEGER NOT NULL,
			mount_slot_index	INTEGER,

			PRIMARY KEY(prisoner_id, vehicle_entity_id)
			FOREIGN KEY(prisoner_id) REFERENCES prisoner(id) ON DELETE CASCADE
			FOREIGN KEY(vehicle_entity_id) REFERENCES entity(id) ON DELETE CASCADE
		);
CREATE TABLE probation_squad_leave_info
		(
			user_id TEXT,
			start_timestamp INTEGER NOT NULL,
			squadmates_left INTEGER NOT NULL,

			PRIMARY KEY(user_id),
			FOREIGN KEY(user_id) REFERENCES user(id) ON DELETE CASCADE
		);
CREATE TABLE quest_cycle_stats
		(
			id               INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
			user_profile_id  INTEGER NOT NULL,
			map_id           INTEGER NOT NULL,

			associated_npc  TEXT,
			sector          TEXT,

			num_completed_quests  INTEGER NOT NULL,

			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE,
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE,
			UNIQUE(user_profile_id, map_id, associated_npc, sector)
		);
CREATE TABLE quest_in_pool
		(
			id             INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
			quest_pool_id  INTEGER NOT NULL,

			quest_mapping_key  TEXT NOT NULL,
			sector             TEXT,
			rewards_index      INTEGER NOT NULL, random_seed INTEGER NOT NULL DEFAULT 0,

			FOREIGN KEY(quest_pool_id) REFERENCES quest_pool(id) ON DELETE CASCADE
		);
CREATE TABLE "quest_lifetime_stats"
		(
			id               INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
			user_profile_id  INTEGER NOT NULL,
			map_id           INTEGER NOT NULL,

			associated_npc              TEXT,
			tier                        INTEGER NOT NULL,
			sector                      TEXT NOT NULL,

			total_num_completed_quests  INTEGER NOT NULL,

			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE,
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE,
			UNIQUE(user_profile_id, map_id, associated_npc, tier, sector)
		);
CREATE TABLE "quest_pool"
		(
			id               INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
			user_profile_id  INTEGER,
			map_id           INTEGER NOT NULL,

			quest_giver_hash           INTEGER NOT NULL,
			quest_giver_bound_user_id  INTEGER,
			quest_giver_type           TEXT NOT NULL,

			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE,
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE,
			UNIQUE(user_profile_id, map_id, quest_giver_type, quest_giver_hash, quest_giver_bound_user_id)
		);
CREATE TABLE quest_refresh
		(
			id               INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
			user_profile_id  INTEGER,
			map_id           INTEGER NOT NULL,

			next_reset_timestamp  REAL NOT NULL DEFAULT 0.0,

			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE,
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE,
			UNIQUE(user_profile_id, map_id)
		);
CREATE TABLE quest_unlocked_tag
		(
			id               INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
			user_profile_id  INTEGER NOT NULL,
			map_id           INTEGER NOT NULL,

			tag  TEXT NOT NULL,

			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE,
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
		);
CREATE TABLE "rain_collector"(
			id						INTEGER NOT NULL,
			map_id					INTEGER NOT NULL,
			user_profile_id			INTEGER,
			amount					REAL,
			fill_rate				REAL,
			max_amount				REAL,
			PRIMARY KEY(user_profile_id, map_id, id),
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE,
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE remote_sensor_registry_key_codes_to_unpair (
			map_id					INTEGER NOT NULL,
			user_profile_id			INTEGER,
			key_code				TEXT NOT NULL,
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE replenishable_resource(
			id							INTEGER NOT NULL,
			map_id						INTEGER NOT NULL,
			user_profile_id				INTEGER,
			desc						BLOB,
			amount						REAL,
			timer						REAL,
			save_time					INTEGER,
			PRIMARY KEY(id)
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE restorable_mesh_instance(
			id					INTEGER NOT NULL,
			map_id				INTEGER NOT NULL,
			user_profile_id		INTEGER,
			component_name		TEXT,
			packed_location		INTEGER,
			location_x			REAL,
			location_y			REAL,
			location_z			REAL,
			rotation_x			REAL,
			rotation_y			REAL,
			rotation_z			REAL,
			rotation_w			REAL,
			scale_x				REAL,
			scale_y				REAL,
			scale_z				REAL,
			restore_interval	REAL,
			restore_timer		REAL,
			save_time			INTEGER,
			PRIMARY KEY(id)
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE "sentries" (
			"id"							INTEGER NOT NULL,
			"user_profile_id"				INTEGER,
			"map_id"						INTEGER,
			"sentry_location_x"				REAL,
			"sentry_location_y"				REAL,
			"sentry_location_z"				REAL,
			"sentry_rotation_yaw"			REAL,
			"sentry_health" 				REAL,
			"sentry_longrangeweapon_ammo"	INTEGER,
			PRIMARY KEY("id")
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE "sentry_ai_controllers" (
			"id"							INTEGER NOT NULL,
			"user_profile_id"				INTEGER,
			"map_id"						INTEGER,
			"sentry_id"						INTEGER,
			"sentry_state"					INTEGER,
			"player_last_known_location_x"	REAL,
			"player_last_known_location_y"	REAL,
			"player_last_known_location_z"	REAL,
			"player_last_known_direction_x"	REAL,
			"player_last_known_direction_y"	REAL,
			"player_last_known_direction_z"	REAL,
			"player_threat_level"			REAL,
			"player_has_entered_hot_zone"	BOOL,
			PRIMARY KEY("id")
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
			FOREIGN KEY(sentry_id) REFERENCES sentries(id) ON DELETE CASCADE
		);
CREATE TABLE "sentry_spawners" (
			"id"							INTEGER NOT NULL,
			"user_profile_id"				INTEGER,
			"map_id"						INTEGER,
			"spawner_location_x"			REAL,
			"spawner_location_y"			REAL,
			"spawner_location_z"			REAL,
			"sentry_id"						INTEGER,
			PRIMARY KEY("id")
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
			FOREIGN KEY(sentry_id) REFERENCES sentries(id) ON DELETE CASCADE
		);
CREATE TABLE server_settings
	(
		enable_item_cooldown_groups		INTEGER NOT NULL DEFAULT 1
	, is_radiation_enabled INTEGER DEFAULT 1);
CREATE TABLE shelter(
			id					INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT
		);
CREATE TABLE "shown_dialogues" (
			map_id						INTEGER NOT NULL,
			user_profile_id				INTEGER,
			dialogue_name				TEXT NOT NULL,
			PRIMARY KEY(user_profile_id, map_id, dialogue_name)
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE "shown_survival_tips" (
			map_id						INTEGER NOT NULL,
			user_profile_id				INTEGER,
			survival_tip_name			TEXT NOT NULL, seen_in_codex BOOLEAN DEFAULT false,
			PRIMARY KEY(user_profile_id, map_id, survival_tip_name)
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE spawned_item_expiration_data (
			id							INTEGER NOT NULL,
			map_id						INTEGER NOT NULL,
			user_profile_id				INTEGER,
			examiner_user_id			TEXT NOT NULL,
			item_asset					TEXT NOT NULL,
			item_expiration_timestamp	INTEGER NOT NULL,

			PRIMARY KEY(id)
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
			FOREIGN KEY(examiner_user_id) REFERENCES user(id) ON DELETE CASCADE
		);
CREATE TABLE squad(
			id						INTEGER NOT NULL,
			name				TEXT, message TEXT, emblem INTEGER, information TEXT, score REAL, member_limit INTEGER, last_member_login_time TEXT, last_member_logout_time TEXT, last_modified_user_id TEXT DEFAULT '',
			PRIMARY KEY(id)
		);
CREATE TABLE squad_member
		(
				id						INTEGER NOT NULL,
				squad_id				INTEGER NOT NULL,
				user_profile_id			INTEGER NOT NULL,
				rank					INTEGER,

				PRIMARY KEY(id)
				FOREIGN KEY(squad_id) REFERENCES squad(id) ON DELETE CASCADE
				FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE survival_stats
		(
			user_profile_id					INTEGER NOT NULL,
			highest_positive_fame_points	REAL DEFAULT 0.0,
			doors_claimed					INTEGER DEFAULT 0,
			animals_killed					INTEGER DEFAULT 0,
			minutes_survived				REAL DEFAULT 0.0,
			kills							INTEGER DEFAULT 0,
			deaths							INTEGER DEFAULT 0,
			locks_picked					INTEGER DEFAULT 0,
			puppets_killed					INTEGER DEFAULT 0,
			guns_crafted					INTEGER DEFAULT 0,
			bullets_crafted					INTEGER DEFAULT 0,
			arrows_crafted					INTEGER DEFAULT 0,
			clothing_crafted				INTEGER DEFAULT 0,
			longest_kill_distance			REAL DEFAULT 0.0,
			melee_kills						INTEGER DEFAULT 0,
			archery_kills					INTEGER DEFAULT 0,
			players_knocked_out				INTEGER DEFAULT 0,
			total_defecations				INTEGER DEFAULT 0,
			total_urinations				INTEGER DEFAULT 0,
			lights_fired					INTEGER DEFAULT 0,
			containers_looted				INTEGER DEFAULT 0,
			items_put_into_containers		INTEGER DEFAULT 0,
			deaths_by_prisoners				INTEGER DEFAULT 0,
			animals_skinned					INTEGER DEFAULT 0,
			food_eaten						REAL DEFAULT 0.0,
			distance_travelled_by_foot		REAL DEFAULT 0.0,
			wounds_patched					INTEGER DEFAULT 0,
			items_picked_up					INTEGER DEFAULT 0,
			liquid_drank					REAL DEFAULT 0.0,
			teeth_lost						INTEGER DEFAULT 0,
			total_calories_intake			INTEGER DEFAULT 0,
			shots_fired						INTEGER DEFAULT 0,
			shots_hit						INTEGER DEFAULT 0,
			headshots						INTEGER DEFAULT 0,
			melee_weapon_swings				INTEGER DEFAULT 0,
			melee_weapon_hits				INTEGER DEFAULT 0,
			melee_weapons_crafted			INTEGER DEFAULT 0,
			drone_kills						INTEGER DEFAULT 0,
			sentry_kills					INTEGER DEFAULT 0,
			prisoner_kills					INTEGER DEFAULT 0,
			puppets_knocked_out				INTEGER DEFAULT 0,
			diarrheas						INTEGER DEFAULT 0,
			vomits							INTEGER DEFAULT 0,
			distance_travelled_in_vehicle	REAL DEFAULT 0.0,
			mushrooms_eaten					INTEGER DEFAULT 0,
			highest_muscle_mass				REAL DEFAULT 0.0,
			highest_fat						REAL DEFAULT 0.0,
			heart_attacks					INTEGER DEFAULT 0,
			overdose						INTEGER DEFAULT 0,
			starvation						INTEGER DEFAULT 0,
			highest_damage_taken			REAL DEFAULT 0.0,
			highest_weight_carried			REAL DEFAULT 0.0,
			lowest_negative_fame_points		REAL DEFAULT 0.0,
			distance_travelled_swimming		REAL DEFAULT 0.0,
			crows_killed					INTEGER DEFAULT 0,
			seagulls_killed					INTEGER DEFAULT 0,
			horses_killed					INTEGER DEFAULT 0,
			boars_killed					INTEGER DEFAULT 0,
			bears_killed					INTEGER DEFAULT 0,
			goats_killed					INTEGER DEFAULT 0,
			deers_killed					INTEGER DEFAULT 0,
			chickens_killed					INTEGER DEFAULT 0,
			rabbits_killed					INTEGER DEFAULT 0,
			donkeys_killed					INTEGER DEFAULT 0,
			times_mauled_by_bear			INTEGER DEFAULT 0,
			longest_animal_kill_distance	REAL DEFAULT 0.0,
			alcohol_drank					INTEGER NOT NULL DEFAULT 0,
			foliage_cut						INTEGER NOT NULL DEFAULT 0,
			distance_travel_by_boat			REAL NOT NULL DEFAULT 0.0,
			distance_sailed					REAL NOT NULL DEFAULT 0.0,
			times_caught_by_shark			INTEGER NOT NULL DEFAULT 0,
			times_escaped_shark_bite		INTEGER NOT NULL DEFAULT 0,
			wolves_killed					INTEGER DEFAULT 0, last_fame_point_award_consecutive_days INTEGER DEFAULT 0, firearm_kills INTEGER DEFAULT 0, bare_handed_kills INTEGER DEFAULT 0,

			PRIMARY KEY(user_profile_id)
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE tracked_quest
		(
			id              INTEGER PRIMARY KEY NOT NULL,
			user_profile_id INTEGER NOT NULL,
			map_id          INTEGER NOT NULL,

			quest_id		INTEGER NOT NULL,
			type			INTEGER NOT NULL,

			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE,
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE,
			FOREIGN KEY(quest_id) REFERENCES tracking_data_set(id) ON DELETE CASCADE,
			UNIQUE(user_profile_id, map_id)
		);
CREATE TABLE tracking_data
		(
			id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,

			tracking_data_set_id  INTEGER NOT NULL,
			data  BLOB, version INTEGER DEFAULT 0, random_seed INTEGER NOT NULL DEFAULT 0,

			FOREIGN KEY(tracking_data_set_id) REFERENCES tracking_data_set(id) ON DELETE CASCADE
		);
CREATE TABLE tracking_data_set
		(
			id               INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
			user_profile_id  INTEGER,
			map_id           INTEGER NOT NULL, sequence_index INTEGER,

			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE,
			FOREIGN KEY(map_id)          REFERENCES map(id)          ON DELETE CASCADE
		);
CREATE TABLE user(
	id								TEXT NOT NULL,
	name							TEXT,
	provider						TEXT,
	last_login_time					TEXT,
	last_direct_connection_address	TEXT, is_banned INTEGER, has_used_new_player_protection BOOLEAN DEFAULT false, creation_time TEXT, id_type TEXT DEFAULT 'Steam', bootflow_completed BOOLEAN DEFAULT FALSE, eos_id TEXT DEFAULT '',
	PRIMARY KEY(id)
);
CREATE TABLE user_favorite_server(
	user_id		TEXT NOT NULL,
	name		TEXT,
	host		TEXT,
	port		INTEGER,
	FOREIGN KEY(user_id) REFERENCES user(id) ON DELETE CASCADE
);
CREATE TABLE user_profile(
	id							INTEGER NOT NULL,
	user_id						TEXT,
	template_xml				TEXT,
	name						TEXT,
	type						INTEGER,
	authority_name				TEXT,
	authority_ip				TEXT,
	authority_response_port		INTEGER,
	authority_gameplay_port		INTEGER,
	authority_user_profile_id	INTEGER,
	authority_auth_token		TEXT,
	last_login_time				TEXT,
	prisoner_id					INTEGER, fame_points REAL, fake_name TEXT, last_logout_time TEXT, money_balance INTEGER, global_spam_protection_state BLOB, local_spam_protection_state BLOB, last_name_change TEXT, favorite_crafting_recipes BLOB, deluxe_version BLOB, play_time INTEGER DEFAULT 0, has_used_new_player_protection BOOLEAN DEFAULT false, creation_time TEXT, authority_is_official BOOLEAN DEFAULT false,
	PRIMARY KEY(id)
	FOREIGN KEY(user_id) REFERENCES user(id) ON DELETE CASCADE
	FOREIGN KEY(prisoner_id) REFERENCES prisoner(id) ON DELETE SET NULL
);
CREATE TABLE "user_profiles_marked_for_deletion"(
			id					INTEGER NOT NULL,
			user_id				INTEGER NOT NULL,
			user_profile_id		INTEGER NOT NULL,
			PRIMARY KEY(id)
			UNIQUE(user_profile_id, user_id),
			FOREIGN KEY(user_id) REFERENCES user(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE user_recent_server(
	user_id		TEXT NOT NULL,
	name		TEXT,
	host		TEXT,
	port		INTEGER,
	FOREIGN KEY(user_id) REFERENCES user(id) ON DELETE CASCADE
);
CREATE TABLE vehicle_entity
		(
			entity_id                  	INTEGER NOT NULL,
			item_container_entity_id	INTEGER,
			data                		BLOB,

			PRIMARY KEY(entity_id)
			FOREIGN KEY(entity_id) REFERENCES entity(id) ON DELETE CASCADE
			FOREIGN KEY(item_container_entity_id) REFERENCES entity(id) ON DELETE SET NULL
		);
CREATE TABLE "vehicle_service"(
			map_id								INTEGER NOT NULL,
			user_profile_id						INTEGER,
			vehicle_id							INTEGER NOT NULL,
			initiator_id						INTEGER,
			service_station_data				BLOB,
			PRIMARY KEY(user_profile_id, map_id, vehicle_id)
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
			FOREIGN KEY(vehicle_id) REFERENCES vehicle_entity(entity_id) ON DELETE CASCADE
		);
CREATE TABLE vehicle_spawner
		(
			vehicle_entity_id                   INTEGER NOT NULL,
			vehicle_asset_id                    TEXT NOT NULL,
			vehicle_alias                       TEXT NOT NULL,
			vehicle_last_access_time            INTEGER NOT NULL,
			is_vehicle_automatically_created    BOOL NOT NULL,
			time_spent_in_forbidden_zone        REAL NOT NULL,
			is_vehicle_functional				BOOL NOT NULL,

			PRIMARY KEY(vehicle_entity_id),
			FOREIGN KEY(vehicle_entity_id) REFERENCES entity(id) ON DELETE CASCADE
		);
CREATE TABLE virtualized_encounters
		(
			id								INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
			global_encounter_manager_id		INTEGER NOT NULL,

			virutalized_encounter_data		BLOB,
			reset_time						REAL,

			FOREIGN KEY(global_encounter_manager_id) REFERENCES global_encounter_manager(id) ON DELETE CASCADE
		);
CREATE TABLE virtualized_item
		(
			item_entity_id 			INTEGER,
			expiration_time 		INTEGER,
			item_name 				TEXT,
			can_expire				INTEGER DEFAULT 1,
			save_time 				INTEGER,
			bounds_size 			REAL,
			queued_visit_time 		INTEGER DEFAULT 0,
			item_user_data 			BLOB,
			owner_user_profile_id 	INTEGER,

			PRIMARY KEY(item_entity_id),
			FOREIGN KEY(item_entity_id) REFERENCES entity(id) ON DELETE CASCADE
			FOREIGN KEY(owner_user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE "weather_parameters" (
			map_id							INTEGER NOT NULL,
			user_profile_id					INTEGER,
			time_of_day						REAL,
			moon_rotation					REAL,
			base_air_temperature			REAL,
			water_temperature				REAL,
			should_cumulonimbus_cause_fog	INTEGER,
			fog_density						REAL,
			data							BLOB,
			PRIMARY KEY(user_profile_id, map_id)
			FOREIGN KEY(map_id) REFERENCES map(id) ON DELETE CASCADE
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE prisoner_disease_immunities
		(
			prisoner_id 						INTEGER NOT NULL,
			disease_name						TEXT NOT NULL,
			last_recovered_play_time			INTEGER,

			PRIMARY KEY(prisoner_id, disease_name)
			FOREIGN KEY(prisoner_id) REFERENCES prisoner(id) ON DELETE CASCADE
		);
CREATE TABLE entity_inventory_component_entry
		(
			entity_component_id		INTEGER NOT NULL,
			entity_id				INTEGER NOT NULL,
			data					INTEGER NOT NULL,
	
			PRIMARY KEY(entity_component_id, entity_id),
			FOREIGN KEY(entity_component_id) REFERENCES entity_component(id) ON DELETE CASCADE
			FOREIGN KEY(entity_id) REFERENCES entity(id) ON DELETE CASCADE
		);
CREATE TABLE entity_component_to_delete_on_startup
		(
			entity_component_id INTEGER NOT NULL,

			PRIMARY KEY(entity_component_id)
			FOREIGN KEY(entity_component_id) REFERENCES entity_component(id) ON DELETE CASCADE
		);
CREATE TABLE custom_zone_configuration_damage_handling_methods
		(
			id								INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
			custom_zone_configuration_id	INTEGER NOT NULL,
			map_id							INTEGER NOT NULL,
			damage_actor_type				INTEGER,
			damage_handling_methods			INTEGER, num_damage_handling_methods INTEGER NOT NULL DEFAULT 0,

			FOREIGN KEY(custom_zone_configuration_id, map_id) REFERENCES custom_zone_configuration(id, map_id) ON DELETE CASCADE
		);
CREATE TABLE ammunition_item_entity
		(
			entity_id 									INTEGER NOT NULL,

			PRIMARY KEY(entity_id)
			FOREIGN KEY(entity_id) REFERENCES entity(id) ON DELETE CASCADE
		);
CREATE TABLE ammunition_data
		(
			id											INTEGER NOT NULL,
			ammunition_item_entity_class 				TEXT NOT NULL,
			health										REAL NOT NULL,
			max_health									REAL NOT NULL,
			default_max_health							REAL NOT NULL,
			state										INTEGER NOT NULL,
			entity_id									INTEGER,
	
			PRIMARY KEY(id)
			FOREIGN KEY(entity_id) REFERENCES entity(id) ON DELETE CASCADE
		);
CREATE TABLE weapon_item_entity
		(
			entity_id 									INTEGER NOT NULL,

			PRIMARY KEY(entity_id)
			FOREIGN KEY(entity_id) REFERENCES entity(id) ON DELETE CASCADE
		);
CREATE TABLE weapon_item_entity_loaded_ammo_data
		(
			weapon_item_entity_id						INTEGER NOT NULL,
			ammunition_data_id							INTEGER NOT NULL,
	
			PRIMARY KEY(weapon_item_entity_id, ammunition_data_id)
			FOREIGN KEY(weapon_item_entity_id) REFERENCES entity(id) ON DELETE CASCADE
			FOREIGN KEY(ammunition_data_id) REFERENCES ammunition_data(id) ON DELETE CASCADE
		);
CREATE TABLE weapon_item_entity_internal_magazine_ammo_data
		(
			weapon_item_entity_id						INTEGER NOT NULL,
			ammunition_data_id							INTEGER NOT NULL,
	
			PRIMARY KEY(weapon_item_entity_id, ammunition_data_id)
			FOREIGN KEY(weapon_item_entity_id) REFERENCES entity(id) ON DELETE CASCADE
			FOREIGN KEY(ammunition_data_id) REFERENCES ammunition_data(id) ON DELETE CASCADE
		);
CREATE TABLE weapon_attachment_item_entity
		(
			entity_id 									INTEGER NOT NULL,

			PRIMARY KEY(entity_id)
			FOREIGN KEY(entity_id) REFERENCES entity(id) ON DELETE CASCADE
		);
CREATE TABLE weapon_attachment_magazine_item_entity
		(
			entity_id 									INTEGER NOT NULL,

			PRIMARY KEY(entity_id)
			FOREIGN KEY(entity_id) REFERENCES entity(id) ON DELETE CASCADE
		);
CREATE TABLE weapon_attachment_magazine_item_entity_ammo_data
		(
			weapon_attachment_magazine_item_entity_id	INTEGER NOT NULL,
			ammunition_data_id							INTEGER NOT NULL,
	
			PRIMARY KEY(weapon_attachment_magazine_item_entity_id, ammunition_data_id)
			FOREIGN KEY(weapon_attachment_magazine_item_entity_id) REFERENCES entity(id) ON DELETE CASCADE
			FOREIGN KEY(ammunition_data_id) REFERENCES ammunition_data(id) ON DELETE CASCADE
		);
CREATE TABLE stackable_component_entry
        (
            entity_component_id     INTEGER NOT NULL,
            foreign_classes_blob BLOB,

            PRIMARY KEY(entity_component_id),
            FOREIGN KEY(entity_component_id) REFERENCES entity_component(id) ON DELETE CASCADE
        );
CREATE TABLE ignitable_component_entry
		(
			entity_component_id INTEGER NOT NULL,
			was_ever_ignited INTEGER,

			PRIMARY KEY(entity_component_id),
			FOREIGN KEY(entity_component_id) REFERENCES entity_component(id) ON DELETE CASCADE
		);
CREATE TABLE entity_decay_manager_entry 
		(
			entity_id			INTEGER NOT NULL,
			timestamp			REAL NOT NULL,
			damage_per_day		REAL NOT NULL,
	
			PRIMARY KEY(entity_id),
			FOREIGN KEY(entity_id) REFERENCES entity(id) ON DELETE CASCADE
		);
CREATE TABLE food_item_entity 
		(
			entity_id								INTEGER NOT NULL,
			damage_over_time_outside_refrigerator	REAL, is_opened INTEGER,
	
			PRIMARY KEY(entity_id),
			FOREIGN KEY(entity_id) REFERENCES entity(id) ON DELETE CASCADE
		);
CREATE TABLE continuous_usage_entity_component 
		(
			entity_component_id		INTEGER NOT NULL,
			amount							FLOAT,
	
			PRIMARY KEY(entity_component_id),
			FOREIGN KEY(entity_component_id) REFERENCES entity_component(id) ON DELETE CASCADE
		);
CREATE TABLE discrete_amount_entity_component 
		(
			entity_component_id		INTEGER NOT NULL,
			quantity							INTEGER NOT NULL,

			PRIMARY KEY(entity_component_id),
			FOREIGN KEY(entity_component_id) REFERENCES entity_component(id) ON DELETE CASCADE
		);
CREATE TABLE animal_bait_feeder_item_entity
		(
			entity_id      INTEGER NOT NULL,
			state          INTEGER,

			PRIMARY KEY(entity_id)
			FOREIGN KEY(entity_id) REFERENCES entity(id) ON DELETE CASCADE
		);
CREATE TABLE blocked_users
		(
			local_user_id		TEXT NOT NULL,
			target_net_id		TEXT NOT NULL,
			FOREIGN KEY(local_user_id) REFERENCES user(id) ON DELETE CASCADE
		);
CREATE TABLE global_aquatic_life_manager
		(
			id					INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
			user_profile_id		INTEGER,
			map_id				INTEGER NOT NULL,
			
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id)      ON DELETE CASCADE,
			FOREIGN KEY(map_id)          REFERENCES map(id)               ON DELETE CASCADE
		);
CREATE TABLE global_aquatic_life_manager_special_volume_types
		(
			id								INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
			global_aquatic_life_manager_id	INTEGER_NOT_NULL,
			volume_type						TEXT,
			activation_timestamp			REAL,			

			FOREIGN KEY(global_aquatic_life_manager_id)	REFERENCES global_aquatic_life_manager(id)	ON DELETE CASCADE
		);
CREATE TABLE global_aquatic_life_manager_special_volumes
		(
			id													INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
			global_aquatic_life_manager_special_volume_type_id	INTEGER_NOT_NULL,
			persistent_id										TEXT,

			FOREIGN KEY(global_aquatic_life_manager_special_volume_type_id)	REFERENCES global_aquatic_life_manager_special_volume_types(id)	ON DELETE CASCADE
		);
CREATE TABLE entity_character_inventory_component
		(
			entity_component_id     	INTEGER NOT NULL,
			last_entity_on_lshoulder    INTEGER,
			last_entity_on_rshoulder    INTEGER,
	
			FOREIGN KEY(entity_component_id) REFERENCES entity_component(id) ON DELETE CASCADE
			FOREIGN KEY(last_entity_on_lshoulder) REFERENCES entity(id) ON DELETE SET NULL
			FOREIGN KEY(last_entity_on_rshoulder) REFERENCES entity(id) ON DELETE SET NULL
		);
CREATE TABLE user_accepted_policies
		(
			user_id TEXT NOT NULL,
			policy_id TEXT NOT NULL,
			accepted_version INTEGER NOT NULL,

			PRIMARY KEY(user_id, policy_id),
			FOREIGN KEY(user_id) REFERENCES user(id) ON DELETE CASCADE
		);
CREATE TABLE apex_facility
		(
			id               INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
			user_profile_id  INTEGER,
			map_id           INTEGER NOT NULL,

			location_x       INTEGER NOT NULL,
			location_y       INTEGER NOT NULL,

			start_time       REAL NOT NULL DEFAULT 0.0, 
			end_time         REAL NOT NULL DEFAULT 0.0, last_end_time REAL NOT NULL DEFAULT 0.0,

			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
			FOREIGN KEY(map_id)          REFERENCES map(id)          ON DELETE CASCADE
		);
CREATE TABLE voice_muted_users
		(
			local_user_id		TEXT NOT NULL,
			target_net_id		TEXT NOT NULL,
			FOREIGN KEY(local_user_id) REFERENCES user(id) ON DELETE CASCADE
		);
CREATE TABLE fish_records
		(
			user_profile_id 						INTEGER NOT NULL,
			fish_mass							FLOAT NOT NULL,
			fish_type							INTEGER NOT NULL,

			PRIMARY KEY(user_profile_id)
			FOREIGN KEY(user_profile_id) REFERENCES user_profile(id) ON DELETE CASCADE
		);
CREATE TABLE apex_facility_mesh_instance_bound_to_activation
		(
			apex_facility_id   INTEGER NOT NULL,
			mesh_instance_name TEXT NOT NULL,
			examine_time       REAL NOT NULL,

			PRIMARY KEY(apex_facility_id, mesh_instance_name)
			FOREIGN KEY(apex_facility_id) REFERENCES apex_facility(id) ON DELETE CASCADE
		);
CREATE TABLE prisoner_applied_spawn_action
		(
			id                 INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
			prisoner_id        INTEGER NOT NULL,
			name               TEXT NOT NULL,

			UNIQUE(prisoner_id, name)
			FOREIGN KEY(prisoner_id) REFERENCES prisoner(id) ON DELETE CASCADE
		);
CREATE TABLE "dog_tag_item_entity"
		(
			entity_id               INTEGER NOT NULL,
			victim_user_profile_id  INTEGER,
			victim_name             TEXT,
			victim_fame_points      INTEGER,

			PRIMARY KEY(entity_id)
			FOREIGN KEY(entity_id) REFERENCES entity(id) ON DELETE CASCADE
			FOREIGN KEY(victim_user_profile_id) REFERENCES user_profile(id) ON DELETE SET NULL
		);
CREATE TABLE custom_zone_deleted_default_regions
		(
			id						INTEGER NOT NULL,
			default_region_name		TEXT,			

			PRIMARY KEY(id)
		);
CREATE INDEX index_on_abandoned_bunker_alarmed_room_for_bunker_id ON abandoned_bunker_alarmed_room(bunker_id);
CREATE INDEX index_on_abandoned_bunker_bcu_terminal_for_bunker_id ON abandoned_bunker_bcu_terminal(bunker_id);
CREATE INDEX index_on_abandoned_bunker_for_user_profile_id_and_map_id ON abandoned_bunker(user_profile_id, map_id);
CREATE INDEX index_on_abandoned_bunker_powered_room_for_bunker_id ON abandoned_bunker_powered_room(bunker_id);
CREATE INDEX index_on_abandoned_bunker_switchboard_fuse_for_bunker_id_and_room_name ON abandoned_bunker_switchboard_fuse(bunker_id, room_name);
CREATE INDEX index_on_abandoned_bunker_switchboard_fuse_for_fuse_id ON abandoned_bunker_switchboard_fuse(fuse_id);
CREATE INDEX index_on_active_quest_for_user_profile_id_and_map_id ON active_quest(user_profile_id, map_id);
CREATE INDEX index_on_active_task_for_available_task_id ON active_task(available_task_id);
CREATE INDEX index_on_active_task_for_user_profile_id_and_map_id ON active_task(user_profile_id, map_id);
CREATE INDEX index_on_bank_account_registry_cards_for_bank_account_id_and_card_type ON bank_account_registry_cards(bank_account_id, card_type);
CREATE INDEX index_on_bank_account_registry_cards_for_card_entity_id ON bank_account_registry_cards(card_entity_id);
CREATE INDEX index_on_bank_account_registry_cards_for_user_profile_id_and_map_id_and_bank_account_id_and_card_type ON bank_account_registry_cards(user_profile_id, map_id, bank_account_id, card_type);
CREATE INDEX index_on_bank_account_registry_currencies_for_bank_account_id_and_currency_type ON bank_account_registry_currencies(bank_account_id, currency_type);
CREATE INDEX index_on_bank_account_registry_currencies_for_user_profile_id_and_map_id_and_bank_account_id_and_currency_type ON bank_account_registry_currencies(user_profile_id, map_id, bank_account_id, currency_type);
CREATE INDEX index_on_bank_account_registry_for_account_owner_user_profile_id ON bank_account_registry(account_owner_user_profile_id);
CREATE INDEX index_on_bank_account_registry_for_user_profile_id_and_map_id_and_account_owner_user_profile_id ON bank_account_registry(user_profile_id, map_id, account_owner_user_profile_id);
CREATE INDEX index_on_bank_account_registry_for_user_profile_id_and_map_id_and_bank_account_number ON bank_account_registry(user_profile_id, map_id, bank_account_number);
CREATE INDEX index_on_bank_general_data_for_user_profile_id_and_map_id ON bank_general_data(user_profile_id, map_id);
CREATE INDEX "index_on_base_element_flag_for_element_id" ON "base_element_flag" (
			"element_id"
		);
CREATE INDEX index_on_base_element_flag_on_overtaker_user_profile_id ON base_element_flag(overtaker_user_profile_id);
CREATE INDEX index_on_base_element_for_base_id ON base_element(base_id);
CREATE INDEX index_on_base_element_for_owner_profile_id ON base_element(owner_profile_id);
CREATE INDEX index_on_base_element_item_for_item_entity_id ON base_element_item(item_entity_id);
CREATE INDEX index_on_base_element_shelter_map_for_shelter_id ON base_element_shelter_map(shelter_id);
CREATE INDEX index_on_base_for_user_profile_id_and_map_id ON base(user_profile_id, map_id);
CREATE INDEX index_on_base_raid_protection_for_manager_id ON base_raid_protection(manager_id);
CREATE INDEX index_on_base_raid_protection_manager_for_user_profile_id ON base_raid_protection_manager(user_profile_id);
CREATE INDEX index_on_bcu_lock_registry_for_bcu_lock_entity_id ON bcu_lock_registry(bcu_lock_entity_id);
CREATE INDEX index_on_bcu_lock_registry_for_user_profile_id_and_map_id ON bcu_lock_registry(user_profile_id, map_id);
CREATE INDEX index_on_chest_acquisition_for_entity_id ON chest_acquisition(entity_id);
CREATE INDEX index_on_cooking_instance_for_bound_utility ON cooking_instance(bound_utility);
CREATE INDEX index_on_cooking_instance_for_manager_id_and_owner_utility_and_tag_index ON cooking_instance(manager_id, owner_utility, tag_index);
CREATE INDEX index_on_cooking_manager_for_user_profile_id_and_map_id ON cooking_manager(user_profile_id, map_id);
CREATE INDEX index_on_custom_zone_configuration_for_map_id ON custom_zone_configuration(map_id);
CREATE INDEX index_on_custom_zone_region_for_map_id ON custom_zone_region(map_id);
CREATE INDEX index_on_economy_for_user_profile_id_and_map_id ON economy(user_profile_id, map_id);
CREATE INDEX index_on_economy_outpost_gold_for_outpost_id ON economy_outpost_gold(outpost_id);
CREATE INDEX index_on_economy_outpost_gold_for_user_profile_id_and_map_id_and_outpost_id ON economy_outpost_gold(user_profile_id, map_id, outpost_id);
CREATE INDEX index_on_economy_outposts_for_user_profile_id_and_map_id_and_outpost_runtime_id ON economy_outposts(user_profile_id, map_id, outpost_runtime_id);
CREATE INDEX index_on_economy_tradeables_info_for_trader_id ON economy_tradeables_info(trader_id);
CREATE INDEX index_on_economy_tradeables_info_for_user_profile_id_and_map_id_and_trader_id_and_tradeable_asset ON economy_tradeables_info(user_profile_id, map_id, trader_id, tradeable_asset);
CREATE INDEX index_on_economy_traders_for_user_profile_id_and_map_id_and_trader_runtime_id ON economy_traders(user_profile_id, map_id, trader_runtime_id);
CREATE UNIQUE INDEX index_on_entity_component_for_entity_id_and_name ON entity_component(entity_id, name);
CREATE INDEX index_on_entity_for_entity_system_id ON entity(entity_system_id);
CREATE INDEX index_on_entity_for_owning_entity_id ON entity(owning_entity_id);
CREATE INDEX index_on_entity_for_parent_entity_id ON entity(parent_entity_id);
CREATE INDEX index_on_entity_system_for_user_profile_id_and_map_id ON entity_system(user_profile_id, map_id);
CREATE INDEX index_on_event_participant_for_event_id ON event_participant(event_id);
CREATE INDEX index_on_event_rankings_cached_for_user_id ON event_rankings_cached(user_id);
CREATE INDEX index_on_event_round_for_event_id ON event_round(
			event_id
		);
CREATE INDEX index_on_event_round_stats_ctf_for_round_id ON event_round_stats_ctf(round_id);
CREATE INDEX index_on_event_round_stats_dm_for_round_id ON event_round_stats_dm(round_id);
CREATE INDEX index_on_event_round_stats_dz_for_round_id ON event_round_stats_dz(round_id);
CREATE INDEX index_on_event_round_stats_for_round_id ON event_round_stats(round_id);
CREATE INDEX index_on_garden_for_garden_manager_id ON garden(garden_manager_id);
CREATE INDEX index_on_garden_for_owner_user_profile_id ON garden(owner_user_profile_id);
CREATE INDEX index_on_garden_manager_for_user_profile_id_and_map_id ON garden_manager(user_profile_id, map_id);
CREATE INDEX index_on_garden_manager_height_relative_sim_data_for_garden_manager_id ON garden_manager_height_relative_sim_data(garden_manager_id);
CREATE INDEX index_on_garden_slot_for_garden_id ON garden_slot(garden_id);
CREATE INDEX index_on_garden_slot_for_planter_user_profile_id ON garden_slot(planter_user_profile_id);
CREATE INDEX index_on_garden_slot_pest_for_garden_slot_id ON garden_slot_pest(garden_slot_id);
CREATE INDEX index_on_global_encounter_manager_bb_data_flags_for_global_encounter_manager_bb_data_id_and_global_encounter_manager_id ON global_encounter_manager_bb_data_flags(global_encounter_manager_bb_data_id, global_encounter_manager_id);
CREATE INDEX index_on_global_encounter_manager_bb_data_flags_for_global_encounter_manager_id_and_flag_id ON global_encounter_manager_bb_data_flags(global_encounter_manager_id, flag_id);
CREATE INDEX index_on_global_encounter_manager_bb_data_for_base_id ON global_encounter_manager_bb_data(base_id);
CREATE INDEX index_on_global_encounter_manager_bb_data_for_global_encounter_manager_id ON global_encounter_manager_bb_data(global_encounter_manager_id);
CREATE INDEX index_on_global_encounter_manager_for_user_profile_id_and_map_id ON global_encounter_manager(user_profile_id, map_id);
CREATE INDEX index_on_global_encounter_manager_rogue_characters_for_global_encounter_manager_id ON global_encounter_manager_rogue_characters(global_encounter_manager_id);
CREATE INDEX index_on_global_encounter_manager_visited_points_for_global_encounter_manager_id ON global_encounter_manager_visited_points(global_encounter_manager_id);
CREATE INDEX index_on_global_encounter_manager_zone_cooldown_for_global_encounter_manager_id ON global_encounter_manager_zone_cooldown(global_encounter_manager_id);
CREATE INDEX index_on_heat_source_for_user_profile_id_and_map_in ON heat_source(user_profile_id, map_id);
CREATE INDEX index_on_item_container_upgrades_for_item_container_id ON item_container_upgrades(
			item_container_id
		);
CREATE INDEX index_on_item_containers_for_user_profile_id_and_map_id_and_runtime_id ON item_containers(user_profile_id, map_id, runtime_id);
CREATE INDEX index_on_item_entity_spawner_cooldown_for_key ON item_entity_spawner_cooldown(key);
CREATE INDEX index_on_item_entity_spawner_entry_for_entity_id ON item_entity_spawner_entry(entity_id);
CREATE INDEX index_on_item_entity_spawner_entry_for_idx ON item_entity_spawner_entry(idx);
CREATE INDEX index_on_item_entity_spawner_for_entity_system_id_and_name ON item_entity_spawner(entity_system_id, name);
CREATE INDEX index_on_item_spawning_cooldown_groups_for_entity_system_id ON item_spawning_cooldown_groups(entity_system_id);
CREATE INDEX index_on_notification_for_user_profile_id ON notification(user_profile_id);
CREATE INDEX index_on_placeable_basebuilding_for_placeable_id ON placeable_basebuilding(placeable_id);
CREATE INDEX index_on_placeable_for_prisoner_id_and_map_id ON placeable(prisoner_id, map_id);
CREATE INDEX index_on_placeable_garden_for_placeable_id ON placeable_garden(placeable_id);
CREATE INDEX index_on_placeable_upgrade_for_placeable_id ON placeable_upgrade(placeable_id);
CREATE INDEX index_on_prisoner_bondage_for_item_entity_id ON prisoner_bondage(item_entity_id);
CREATE INDEX index_on_prisoner_entity_for_prisoner_id ON prisoner_entity(prisoner_id);
CREATE INDEX index_on_prisoner_for_gender_and_appearance_index ON prisoner(gender, appearance_index);
CREATE INDEX index_on_prisoner_for_user_profile_id ON prisoner(user_profile_id);
CREATE INDEX index_on_prisoner_skill_for_prisoner_id ON prisoner_skill(prisoner_id);
CREATE INDEX index_on_prisoner_vehicle_mountee_info_for_vehicle_entity_id ON prisoner_vehicle_mountee_info(vehicle_entity_id);
CREATE INDEX index_on_quest_in_pool_for_quest_pool_id ON quest_in_pool(quest_pool_id);
CREATE INDEX index_on_quest_unlocked_tag_for_user_profile_id_and_map_id ON quest_unlocked_tag(user_profile_id, map_id);
CREATE INDEX index_on_remote_sensor_registry_key_codes_to_unpair_for_user_profile_id_and_map_id ON remote_sensor_registry_key_codes_to_unpair(user_profile_id, map_id);
CREATE INDEX index_on_replenishable_resource_for_user_profile_id_and_map_id ON replenishable_resource(user_profile_id, map_id);
CREATE INDEX index_on_restorable_mesh_instance_for_user_profile_id_and_map_id ON restorable_mesh_instance(user_profile_id, map_id);
CREATE INDEX index_on_sentries_for_user_profile_id_and_map_id ON sentries(user_profile_id, map_id);
CREATE INDEX index_on_sentry_ai_controllers_for_sentry_id ON sentry_ai_controllers(sentry_id);
CREATE INDEX index_on_sentry_ai_controllers_for_user_profile_id_and_map_id ON sentry_ai_controllers(user_profile_id, map_id);
CREATE INDEX index_on_sentry_spawners_for_sentry_id ON sentry_spawners(sentry_id);
CREATE INDEX index_on_sentry_spawners_for_user_profile_id_and_map_id ON sentry_spawners(user_profile_id, map_id);
CREATE INDEX index_on_spawned_item_expiration_data_for_examiner_user_id ON spawned_item_expiration_data(examiner_user_id);
CREATE INDEX index_on_spawned_item_expiration_data_for_user_profile_id_and_map_id ON spawned_item_expiration_data(user_profile_id, map_id);
CREATE INDEX index_on_squad_for_name ON squad(name);
CREATE INDEX index_on_squad_member_for_squad_id ON squad_member(squad_id);
CREATE INDEX index_on_squad_member_for_user_profile_id ON squad_member(user_profile_id);
CREATE INDEX index_on_tracked_quest_for_quest_id ON tracked_quest(quest_id);
CREATE INDEX index_on_tracking_data_for_tracking_data_set_id ON tracking_data(tracking_data_set_id);
CREATE INDEX index_on_tracking_data_set_for_user_profile_id ON tracking_data_set(user_profile_id);
CREATE INDEX index_on_user_favorite_server_for_user_id ON user_favorite_server(user_id);
CREATE INDEX index_on_user_profile_for_prisoner_id ON user_profile(prisoner_id);
CREATE INDEX index_on_user_profile_for_type ON user_profile(type);
CREATE INDEX index_on_user_profile_for_user_id_and_type_and_name ON user_profile(user_id, type, name);
CREATE INDEX index_on_user_profiles_marked_for_deletion_for_user_id ON user_profiles_marked_for_deletion(user_id);
CREATE INDEX index_on_user_profiles_marked_for_deletion_for_user_profile_id ON user_profiles_marked_for_deletion(user_profile_id);
CREATE INDEX index_on_user_recent_server_for_user_id ON user_recent_server(user_id);
CREATE INDEX index_on_vehicle_entity_for_item_container_entity_id ON vehicle_entity(item_container_entity_id);
CREATE INDEX index_on_vehicle_service_for_vehicle_id ON vehicle_service(vehicle_id);
CREATE INDEX index_on_virtualized_encounters_for_global_encounter_manager_id ON virtualized_encounters(global_encounter_manager_id);
CREATE INDEX index_on_virtualized_item_for_owner_user_profile_id ON virtualized_item(owner_user_profile_id);
CREATE INDEX index_on_ammunition_data_for_entity_id ON ammunition_data(entity_id);
CREATE INDEX index_on_entity_inventory_component_entry_for_entity_id ON entity_inventory_component_entry(entity_id);
CREATE INDEX index_on_weapon_attachment_magazine_item_entity_ammo_data_for_ammunition_data_id ON weapon_attachment_magazine_item_entity_ammo_data(ammunition_data_id);
CREATE INDEX index_on_weapon_item_entity_internal_magazine_ammo_data_for_ammunition_data_id ON weapon_item_entity_internal_magazine_ammo_data(ammunition_data_id);
CREATE INDEX index_on_weapon_item_entity_loaded_ammo_data_for_ammunition_data_id ON weapon_item_entity_loaded_ammo_data(ammunition_data_id);
CREATE INDEX index_on_prisoner_inventory_quick_access_slot_for_item_entity_id ON prisoner_inventory_quick_access_slot(item_entity_id);
CREATE INDEX index_on_blocked_users_for_local_user_id ON blocked_users(local_user_id);
CREATE UNIQUE INDEX index_on_quest_pool_for_null_user_profile 
			ON quest_pool (map_id, quest_giver_type, quest_giver_hash, quest_giver_bound_user_id)
			WHERE user_profile_id IS NULL;
CREATE UNIQUE INDEX index_on_apex_facility_for_non_null_user_profile 
			ON apex_facility (user_profile_id, map_id, location_x, location_y)
			WHERE user_profile_id IS NOT NULL;
CREATE UNIQUE INDEX index_on_apex_facility_for_null_user_profile 
			ON apex_facility (map_id, location_x, location_y)
			WHERE user_profile_id IS NULL;
CREATE INDEX index_on_dog_tag_item_entity_for_victim_user_profile_id ON dog_tag_item_entity(victim_user_profile_id);
CREATE TRIGGER event_rankings_cached_update UPDATE ON event_rankings_cached
		BEGIN
			UPDATE events_stats SET enemy_kills = new.enemy_kills, deaths = new.deaths, ctf_captures = new.ctf_captures, team_kills = new.team_kills
			WHERE user_profile_id = new.user_profile_id;
		END;
CREATE TRIGGER trigger_prisoner_entity_delete DELETE ON prisoner_entity
		BEGIN
			DELETE FROM entity WHERE id = OLD.entity_id;
		END;
CREATE TRIGGER trigger_user_profile_insert AFTER INSERT ON user_profile
		WHEN new.type != 2
		BEGIN
			INSERT INTO event_rankings_cached(user_id, user_profile_id, name, is_banned, fame_points) VALUES(new.user_id, new.id, coalesce(new.name, ''), false, coalesce(new.fame_points, 0));
			INSERT INTO events_stats(user_profile_id) VALUES(new.id);
			INSERT INTO survival_stats(user_profile_id) VALUES(new.id);
			INSERT INTO fishing_stats(user_profile_id) VALUES(new.id);
		END;
CREATE TRIGGER trigger_user_profile_update_fame_points UPDATE OF fame_points ON user_profile
		BEGIN
			UPDATE event_rankings_cached SET fame_points = new.fame_points WHERE user_profile_id = new.id;
		END;
CREATE TRIGGER trigger_user_profile_update_name UPDATE OF name ON user_profile
		BEGIN
			UPDATE event_rankings_cached SET name = new.name WHERE user_profile_id = new.id;
		END;
CREATE TRIGGER trigger_event_rankings_cached_is_banned UPDATE OF is_banned ON user	BEGIN	UPDATE event_rankings_cached SET is_banned = new.is_banned WHERE user_id = new.id;	END;
