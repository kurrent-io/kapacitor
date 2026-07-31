# cursor-agent bundle scan — subagentStart evidence

Version: 2026.07.23-e383d2b   Host: macOS arm64   Scanned: 2026-07-30
Install root (not vendored): ~/.local/share/cursor-agent/versions/2026.07.23-e383d2b/

## Extraction command
```
D=~/.local/share/cursor-agent/versions/2026.07.23-e383d2b
grep -rlo "subagentStart" "$D"
python3 -c "import re,sys; s=open(sys.argv[1],errors=\"replace\").read();
  [print(s[max(0,m.start()-260):m.end()+260]) for m in re.finditer(r\"subagentStart\", s)]" "$D/index.js"
```

## SHA-256 of the files containing the symbol
39a3fbb76b3382d2ffa82f6a158f292ae4fe0ba06162795dcbbacde325ca9853  index.js
f18443079193edcf4036be6e2624b0e9f910c3672977bff42e07a22f3a7f7c33  cursor-agent-svc.js
8f19019210a149ab091d8029a685e5f0135aea2fbb8edd9d9aba95a8f29ed3f9  3143.index.js

## Bounded excerpts (verbatim, from index.js)
```js
// payload builder + dispatch
case"subagentStart":{const e=x.request.value,t=Object.assign(Object.assign({conversation_id:null!==(s=e.conversationId)&&void 0!==s?s:"",generation_id:null!==(a=e.generationId)&&void 0!==a?a:"",model:null!==(o=e.model)&&void 0!==o?o:""},(0,Ie.U4)(e)),{subagent_id:e.subagentId,subagent_type:e.subagentType,task:e.task,parent_conversation_id:e.parentConversationId,tool_call_id:e.toolCallId,subagent_model:e.subagentModel,is_parallel_worker:e.isParallelWorker,git_branch:e.gitBranch}),n=yield this.hookExecutor.executeHookForStep(Ie._E.subagentStart,t);return new i.S1({response:new i.w5({response:{case:"subagentStart",value:new me.SubagentStartRequestResponse({permission:null==n?void 0:n.permission,userMessage:null==n?void 0:n.user_message,additionalContext:null==n?void 0:n.a
```
```js
// hook-name registry (shows the full set of events the CLI knows)
",afterAgentResponse:"afterAgentResponse",afterAgentThought:"afterAgentThought",sessionStart:"sessionStart",sessionEnd:"sessionEnd",preCompact:"preCompact",subagentStart:"subagentStart",subagentStop:"subagentStop",preToolUse:"preToolUse",postToolUse:"postToolUse",postToolUseFailure:"postToolUseFailure",workspaceOpen:"workspaceOpen"},s={PreToolUse:r.preToolUse,PermissionRequest:null,PostToolUse:r.postTool
```
