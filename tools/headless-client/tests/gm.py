"""The game master's commands for the walks: the slash service's own words, typed by the pilot's session.

A walk seeds what a game master would give (ISK, standings, skills) through these instead of a database write, where the server
has the command. eve.rs (the slash service's SlashCmd) and EVE.js (chatCommands.js) answer the same words, so one walk seeds either
server. The pilot must be selected: the commands act on the caller's character. A seed with no command (the GM role, a hull, a
safety level) stays in dbseed.py.
"""

# The answers that say the command was not run (a usage line, an unknown word, no character, no role): a walk must not go on.
REFUSED = ("Usage:", "Unknown command", "Select a character", "Command requires a game master role")

class GmError(RuntimeError):
    """The server did not run the command; its answer is the message."""

def command(client, text: str) -> str:
    """Sends a slash command for the selected character and returns the server's answer."""
    answer = client.call("slash", "SlashCmd", text)
    if isinstance(answer, bytes):
        answer = answer.decode("utf-8")
    answer = str(answer)
    if answer.startswith(REFUSED):
        raise GmError(f"{text!r} was not run: {answer}")
    return answer

def set_isk(client, amount: int) -> str:
    """The pilot's ISK becomes the amount (/setisk, the absolute balance on both servers)."""
    return command(client, f"/setisk {int(amount)}")

def set_standing(client, owner: int, value: float = 10) -> str:
    """The pilot's standing toward an owner by its id (an agent's corporation, a faction or a corporation): /setstanding <value> <owner id>."""
    return command(client, f"/setstanding {value:g} {int(owner)}")

def set_agent_standing(client, agent_id: int, value: float = 10, *, name: str | None = None) -> str:
    """The pilot's personal standing toward an agent. EVE.js takes the agent by its name (a number does not resolve there), so a walk
    against it passes the name; eve.rs takes the agent's id (DEVIATIONS G2: an agent has no name index)."""
    token = f'"{name}"' if name is not None else str(int(agent_id))
    return command(client, f"/setstanding {value:g} {token}")

def give_skill(client, skill: int | str, level: int) -> str:
    """A skill to the pilot at a level (/giveskill me <skill> <level>)."""
    return command(client, f"/giveskill me {skill} {int(level)}")

def give_skills(client, level: int = 5) -> str:
    """Every skill to the pilot at a level (/giveskill me all <level>)."""
    return command(client, f"/giveskill me all {int(level)}")

def heal(client) -> str:
    """The pilot's ship is mended: shield, armor, hull and capacitor (/heal)."""
    return command(client, "/heal")
