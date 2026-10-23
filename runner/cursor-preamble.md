# Factory worker instructions for the Cursor agent

The factory plugin's skills were written for Claude Code. This section says how a Cursor agent session follows them; where a skill and this
section differ, this section wins.

- There are no subagents in this session and no Agent or Task tool. Where a skill says to start a subagent (analyst, implementer, reviewer,
  inspector), do that role's work yourself, inline and in order: read its definition in {{BASE}}/plugin/agents/<role>.md, do the role's
  work, write the result the skill names, then continue as the orchestrator. Finish one role before starting the next. A missing subagent is
  never a reason to stop, release or block the item.
- Skip `factory.ps1 model-for --role subagent`: the model of this session is fixed by the container.
- The reviewer role still reviews: before completing a column, re-read the whole diff with the reviewer definition's checklist as if someone
  else wrote it, and fix what it finds. Never skip a review because the same session wrote the code.
- Nobody answers questions in this session: questions go to the supervisor through factory.ps1, as factory-peer-messaging says.

