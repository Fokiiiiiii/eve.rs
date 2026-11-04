use super::*;

#[test]
fn modifiers_of_one_attribute_stack_with_the_games_penalty_from_the_strongest_down() {
    // one web is the whole of it, a second counts 86.9%, a third 57.1%, a fourth 28.3%
    assert!((stacked([-50.0]) - 0.5).abs() < 1e-9);
    let two = stacked([-50.0, -50.0]);
    assert!((two - 0.5 * (1.0 - 0.5 * penalty(1))).abs() < 1e-9, "{two}");
    assert!(
        (penalty(1) - 0.869_119).abs() < 1e-5
            && (penalty(2) - 0.570_584).abs() < 1e-5
            && (penalty(3) - 0.282_950).abs() < 1e-5
    );
    // the strongest counts in full whatever the order, and what raises and what lowers are worked apart
    assert_eq!(stacked([-30.0, -60.0]), stacked([-60.0, -30.0]));
    let mixed = stacked([25.0, -50.0]);
    assert!((mixed - 1.25 * 0.5).abs() < 1e-9, "{mixed}");
    assert_eq!(stacked([]), 1.0);
}

#[test]
fn disruptions_of_the_turrets_stack_with_the_penalty_and_lapse_with_their_records() {
    let mut incoming = Incoming::default();
    assert_eq!(incoming.weapon_factors(), (1.0, 1.0, 1.0));
    let disrupt = |range, falloff, tracking| Lasting::Disrupt {
        range_percent: range,
        falloff_percent: falloff,
        tracking_percent: tracking,
    };
    incoming.upsert(1, 10, disrupt(-20.0, -10.0, -30.0), 100);
    let one = incoming.weapon_factors();
    assert!(
        (one.0 - 0.8).abs() < 1e-9 && (one.1 - 0.9).abs() < 1e-9 && (one.2 - 0.7).abs() < 1e-9,
        "{one:?}"
    );
    incoming.upsert(2, 11, disrupt(-20.0, -10.0, -30.0), 100);
    let two = incoming.weapon_factors();
    // the second of a kind counts for a little less than the first
    assert!((two.0 - 0.8 * (1.0 - 0.2 * penalty(1))).abs() < 1e-9, "{two:?}");
    assert!(two.0 < one.0 && two.2 < one.2);
    // the numbers of a ship's own are not touched by it, only the turrets are
    let base = Base {
        max_velocity: 400.0,
        signature_radius: 40.0,
        max_target_range: 30_000.0,
        scan_resolution: 500.0,
        sensor_strength: [10.0, 20.0, 30.0, 40.0],
    };
    assert_eq!(incoming.worked(&base), base);
}

#[test]
fn guidance_disruptions_stack_with_the_penalty_and_work_on_the_missiles_alone() {
    use crate::missiles::Guidance;
    let mut incoming = Incoming::default();
    assert_eq!(incoming.missile_factors(), Guidance::NONE);
    let disrupt = |velocity, delay, aoe_velocity, aoe_cloud| Lasting::GuidanceDisrupt {
        velocity_percent: velocity,
        delay_percent: delay,
        aoe_velocity_percent: aoe_velocity,
        aoe_cloud_percent: aoe_cloud,
    };
    incoming.upsert(1, 10, disrupt(-10.0, -10.0, -20.0, 20.0), 100);
    let one = incoming.missile_factors();
    assert!(
        (one.velocity - 0.9).abs() < 1e-9
            && (one.flight_time - 0.9).abs() < 1e-9
            && (one.explosion_velocity - 0.8).abs() < 1e-9
            && (one.explosion_radius - 1.2).abs() < 1e-9,
        "{one:?}"
    );
    // a second counts for a little less than the first, in the direction of each (the radius grows, the rest shrink)
    incoming.upsert(2, 11, disrupt(-10.0, -10.0, -20.0, 20.0), 100);
    let two = incoming.missile_factors();
    assert!((two.velocity - 0.9 * (1.0 - 0.1 * penalty(1))).abs() < 1e-9, "{two:?}");
    assert!(two.velocity < one.velocity && two.explosion_radius > one.explosion_radius);
    // the turrets of the ship are not touched by it
    assert_eq!(incoming.weapon_factors(), (1.0, 1.0, 1.0));
}

#[test]
fn a_record_is_renewed_by_the_same_module_and_the_numbers_are_worked_from_the_base() {
    let base = Base {
        max_velocity: 400.0,
        signature_radius: 40.0,
        max_target_range: 30_000.0,
        scan_resolution: 500.0,
        sensor_strength: [10.0, 20.0, 30.0, 40.0],
    };
    let mut incoming = Incoming::default();
    incoming.upsert(1, 10, Lasting::Web { percent: -60.0 }, 100);
    incoming.upsert(1, 10, Lasting::Web { percent: -40.0 }, 200);
    incoming.upsert(2, 10, Lasting::Paint { percent: 25.0 }, 100);
    incoming.upsert(
        3,
        11,
        Lasting::Damp {
            range_percent: -20.0,
            resolution_percent: -30.0,
            gravimetric_percent: 0.0,
            ladar_percent: 0.0,
            magnetometric_percent: 0.0,
            radar_percent: 0.0,
        },
        100,
    );
    let worked = incoming.worked(&base);
    assert!((worked.max_velocity - 240.0).abs() < 1e-9, "{worked:?}");
    assert!((worked.signature_radius - 50.0).abs() < 1e-9);
    assert!(
        (worked.max_target_range - 24_000.0).abs() < 1e-9 && (worked.scan_resolution - 350.0).abs() < 1e-9
    );
    assert!(!incoming.scrambled_with(0.0));
    incoming.upsert(4, 12, Lasting::Scramble { strength: 2.0, blocks_mwd: true }, 100);
    incoming.upsert(5, 13, Lasting::Scramble { strength: 1.0, blocks_mwd: false }, 100);
    assert_eq!(incoming.scramble_points(), 3.0);
    assert!(incoming.scrambled_with(0.0));
}

#[test]
fn a_positive_remote_sensor_boost_stacks_refreshes_and_expires_across_all_sensor_channels() {
    let base = Base {
        max_velocity: 400.0,
        signature_radius: 40.0,
        max_target_range: 30_000.0,
        scan_resolution: 500.0,
        sensor_strength: [10.0, 20.0, 30.0, 40.0],
    };
    let session = crate::scene::tests::SESSION;
    let mut scene = Scene::new(crate::scene::tests::system());
    scene.undock(session, crate::scene::tests::spec(), crate::scene::tests::STATION, 100).unwrap();
    scene.pilots.get_mut(&session).unwrap().incoming.base = Some(base);

    let boost = Lasting::Damp {
        range_percent: 25.0,
        resolution_percent: 35.0,
        gravimetric_percent: 20.0,
        ladar_percent: 30.0,
        magnetometric_percent: 40.0,
        radar_percent: 50.0,
    };
    let second_boost = Lasting::Damp {
        range_percent: 0.0,
        resolution_percent: 0.0,
        gravimetric_percent: 20.0,
        ladar_percent: 20.0,
        magnetometric_percent: 20.0,
        radar_percent: 20.0,
    };
    let negative = Lasting::Damp {
        range_percent: -50.0,
        resolution_percent: -40.0,
        gravimetric_percent: 0.0,
        ladar_percent: 0.0,
        magnetometric_percent: 0.0,
        radar_percent: 0.0,
    };
    {
        let incoming = &mut scene.pilots.get_mut(&session).unwrap().incoming;
        incoming.upsert(1, 10, boost, 100);
        incoming.upsert(1, 10, boost, 200);
        incoming.upsert(2, 11, second_boost, 250);
        incoming.upsert(3, 12, negative, 300);
        let boosts: Vec<_> = incoming.records.iter().filter(|r| r.source == 1 && r.module == 10).collect();
        assert_eq!(boosts.len(), 1);
        assert_eq!(boosts[0].until, 200);
        let worked = incoming.worked(&base);
        assert!((worked.max_target_range - 18_750.0).abs() < 1e-9, "{worked:?}");
        assert!((worked.scan_resolution - 405.0).abs() < 1e-9, "{worked:?}");
        let once = [12.0, 26.0, 42.0, 60.0];
        let unpenalized = [14.4, 31.2, 50.4, 72.0];
        for index in 0..4 {
            assert!(worked.sensor_strength[index] > once[index], "{worked:?}");
            assert!(worked.sensor_strength[index] < unpenalized[index], "{worked:?}");
        }
        let radar_jammer = [0.0, 0.0, 0.0, 40.0];
        assert!(
            jam_chance(&radar_jammer, &worked.sensor_strength, 1.0, 1.0)
                < jam_chance(&radar_jammer, &base.sensor_strength, 1.0, 1.0)
        );
    }

    assert!(scene.expire_incoming(session, 199).is_empty());
    let incoming = &scene.pilots[&session].incoming;
    assert!(incoming.records.iter().any(|r| r.source == 1 && r.module == 10));
    assert!(incoming.records.iter().any(|r| r.source == 2 && r.module == 11));

    scene.expire_incoming(session, 200);
    let pilot = &scene.pilots[&session];
    assert_eq!(pilot.incoming.records.len(), 2);
    for (actual, expected) in pilot.ship.spec.sensor_strength.iter().zip([12.0, 24.0, 36.0, 48.0]) {
        assert!((actual - expected).abs() < 1e-9);
    }

    scene.expire_incoming(session, 250);
    let pilot = &scene.pilots[&session];
    assert_eq!(pilot.incoming.records.len(), 1);
    assert_eq!(pilot.incoming.records[0].what, negative);
    for (actual, expected) in pilot.ship.spec.sensor_strength.iter().zip(base.sensor_strength) {
        assert!((actual - expected).abs() < 1e-9);
    }
}

#[test]
fn jam_chance_obeys_ecm_resistance_and_strength_boundaries() {
    let sensor = [0.0, 0.0, 0.0, 20.0];
    let normal_jammer = [0.0, 0.0, 0.0, 10.0];

    assert_eq!(jam_chance(&normal_jammer, &sensor, 0.0, 1.0), 0.0);
    assert_eq!(jam_chance(&normal_jammer, &sensor, 0.00001, 1.0), 0.000005);
    assert_eq!(jam_chance(&normal_jammer, &sensor, 0.5, 1.0), 0.25);
    assert_eq!(jam_chance(&normal_jammer, &sensor, 1.0, 1.0), 0.5);
    assert_eq!(jam_chance(&normal_jammer, &sensor, -1.0, 1.0), 0.0);
    assert_eq!(jam_chance(&normal_jammer, &sensor, 2.0, 1.0), 0.5);

    let equal_strength_jammer = [0.0, 0.0, 0.0, 20.0];
    assert_eq!(jam_chance(&equal_strength_jammer, &sensor, 1.0, 1.0), 1.0);
    let stronger_jammer = [0.0, 0.0, 0.0, 40.0];
    assert_eq!(jam_chance(&stronger_jammer, &sensor, 1.0, 1.0), 1.0);
    let no_sensor = [0.0; 4];
    assert_eq!(jam_chance(&stronger_jammer, &no_sensor, 1.0, 1.0), 0.0);

    let no_jammer = [0.0; 4];
    assert_eq!(jam_chance(&no_jammer, &sensor, 1.0, 1.0), 0.0);
    let non_finite_jammer = [0.0, 0.0, 0.0, f64::NAN];
    assert_eq!(jam_chance(&non_finite_jammer, &sensor, 1.0, 1.0), 0.0);
    let infinite_sensor = [0.0, 0.0, 0.0, f64::INFINITY];
    assert_eq!(jam_chance(&normal_jammer, &infinite_sensor, 1.0, 1.0), 0.0);
    // EVE.js falls back to Dogma's default ECMResistance of 1 for a non-finite target value.
    assert_eq!(jam_chance(&normal_jammer, &sensor, f64::NAN, 1.0), 0.5);
}

#[test]
fn ecm_burst_uses_surface_radius_and_rejects_invalid_geometry() {
    let origin = [0.0, 0.0, 0.0];
    // 1021 m center distance minus two 10 m hulls is exactly radius + the 1 m wire tolerance.
    assert!(burst_reaches(origin, 10.0, [1021.0, 0.0, 0.0], 10.0, 1000.0));
    assert!(!burst_reaches(origin, 10.0, [1022.01, 0.0, 0.0], 10.0, 1000.0));
    assert!(burst_reaches(origin, 10.0, [0.0, 0.0, 0.0], 10.0, 1.0));
    assert!(!burst_reaches(origin, 10.0, [0.0, 0.0, 0.0], 10.0, 0.0));
    assert!(!burst_reaches(origin, 10.0, [f64::NAN, 0.0, 0.0], 10.0, 1000.0));
}
