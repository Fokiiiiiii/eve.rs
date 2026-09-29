//! The bodies of a solar system, built from the static data, hold to what the reference sent the client for it.

mod support;

use eve_space::sde::Sde;
use eve_space::world::{BodyKind, World};
use support::*;

const SYSTEM: i32 = 30100032;

fn world() -> Option<World> {
    let Some(sde) = Sde::locate() else {
        eprintln!("no static data found (EVE_SDE_DIR or .cache/sde): skipped");
        return None;
    };
    Some(World::load(sde).expect("the static data loads"))
}

#[test]
fn the_bodies_are_the_balls_the_reference_sent_in_the_same_order() {
    let Some(world) = world() else { return };
    let recording = fixture("undock.json");
    let race = &recording["races"][0];
    let state = set_state(step(race, "bind the ballpark"));
    let buffer = unhex(state["state"]["$buffer"].as_str().unwrap());
    // what the reference adds beyond the static data (customs offices, authored props) has ids from 1.2e12; the ship is free
    let recorded: Vec<RecordedBall> =
        rigid_balls(&buffer).into_iter().filter(|b| b.id < 1_000_000_000_000).collect();

    let system = world.system(SYSTEM).unwrap();
    assert_eq!(system.name, "Manifest");
    assert_eq!(
        system.bodies.iter().map(|b| b.id).collect::<Vec<_>>(),
        recorded.iter().map(|b| b.id).collect::<Vec<_>>()
    );
    for (body, ball) in system.bodies.iter().zip(&recorded) {
        assert_eq!(body.radius as f32, ball.radius, "radius of {} {}", body.id, body.name);
        assert_eq!(
            [body.position.x, body.position.y, body.position.z],
            ball.position,
            "position of {} {}",
            body.id,
            body.name
        );
        let flags = match body.kind {
            BodyKind::Station | BodyKind::Stargate => 6,
            _ => 2,
        };
        assert_eq!(flags, ball.flags, "flags of {} {}", body.id, body.name);
    }
}

#[test]
fn the_names_and_kinds_are_the_ones_the_reference_gave_each_body() {
    let Some(world) = world() else { return };
    let recording = fixture("undock.json");
    let state = set_state(step(&recording["races"][0], "bind the ballpark"));
    let system = world.system(SYSTEM).unwrap();
    let mut checked = 0;
    for slim in state["slims"].as_array().unwrap() {
        let fields = &slim["args"]["$dict"];
        let field =
            |name: &str| fields.as_array().unwrap().iter().find(|p| p[0] == name).map(|p| p[1].clone());
        let id = number(&field("itemID").unwrap());
        let Some(body) = system.body(id) else { continue };
        assert_eq!(field("name").unwrap().as_str().unwrap(), body.name, "name of {id}");
        assert_eq!(number(&field("typeID").unwrap()), i64::from(body.type_id), "type of {id}");
        assert_eq!(number(&field("groupID").unwrap()), i64::from(body.group_id), "group of {id}");
        assert_eq!(number(&field("categoryID").unwrap()), i64::from(body.category_id), "category of {id}");
        assert_eq!(number(&field("ownerID").unwrap()), body.owner_id, "owner of {id}");
        checked += 1;
    }
    assert_eq!(checked, system.bodies.len(), "every body has a slim item in the recording");
}

#[test]
fn a_stargate_faces_the_way_the_reference_turned_it() {
    let Some(world) = world() else { return };
    let recording = fixture("undock.json");
    let state = set_state(step(&recording["races"][0], "bind the ballpark"));
    let system = world.system(SYSTEM).unwrap();
    let mut checked = 0;
    for slim in state["slims"].as_array().unwrap() {
        let fields = &slim["args"]["$dict"];
        let field =
            |name: &str| fields.as_array().unwrap().iter().find(|p| p[0] == name).map(|p| p[1].clone());
        let id = number(&field("itemID").unwrap());
        let Some(body) = system.body(id) else { continue };
        let eve_space::world::Detail::Stargate { facing, .. } = &body.detail else { continue };
        let recorded: Vec<f64> = field("dunRotation").unwrap()["$tuple"]
            .as_array()
            .unwrap()
            .iter()
            .map(|v| v.as_f64().unwrap())
            .collect();
        assert_eq!(facing.expect("a gate has a facing").to_vec(), recorded, "facing of {id}");
        checked += 1;
    }
    assert!(checked >= 2, "the recorded system has gates, saw {checked}");
}
