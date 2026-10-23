# Octopus project cmdemo1-fworker-runtime: runbooks that start AI worker containers on self-hosted machines with Octopus tentacles
# (deploy/hosts/install-fworker-host.sh registers them as deployment targets with tag fworker-host in tdd), and an overflow runbook on the
# Hosted Ubuntu dynamic worker pool for short roles. The gateway's Octopus runtime adapter runs these runbooks with its service account.
#
#   export OCTOPUS_URL=https://clearmeasure.octopus.app OCTOPUS_APIKEY=...   # an API key of a space manager, never committed
#   terraform init && terraform plan && terraform apply
#
# The trigger "Reconcile hosts every 5 minutes" stays disabled until -var reconcile_enabled=true (with the gateway's octopus runtime on).
# The feed's credential comes from -var ghcr_username=<user> and TF_VAR_ghcr_token (read:packages); the user also becomes Fworker.GhcrUsername.
#
# The sensitive variables (local.sensitive_names) are not Terraform's: provider 1.21 refuses to create a sensitive variable without a value,
# and a value in Terraform would sit in the state. Add them in the Octopus portal (Project > Variables) or the API as empty Sensitive
# variables after the apply, then set the values there; the scripts treat an empty or missing value as unset. A state from before this
# change holds octopusdeploy_variable.sensitive: `terraform state rm octopusdeploy_variable.sensitive` first, or the apply deletes them.
# The scripts are deploy/octopus/scripts/*.sh (tested by tests/octopus/runbooks.test.sh).

terraform {
  required_version = ">= 1.6"
  required_providers {
    octopusdeploy = {
      source  = "OctopusDeploy/octopusdeploy"
      version = "~> 1.21"
    }
  }
}

provider "octopusdeploy" {
  # address and api_key from OCTOPUS_URL and OCTOPUS_APIKEY
  space_id = var.space_id
}

# the default of the prompted Fworker.Agent; the gateway passes its runtimes.octopus.agent at every run
variable "default_agent" {
  type    = string
  default = "cursor"
  validation {
    condition     = contains(["claude", "cursor", "codex"], var.default_agent)
    error_message = "default_agent must be claude, cursor or codex."
  }
}

variable "space_id" {
  type    = string
  default = "Spaces-355"
}

variable "environment_name" {
  type    = string
  default = "tdd"
}

variable "target_tag" {
  type    = string
  default = "fworker-host"
}

variable "hosted_worker_pool_name" {
  type    = string
  default = "Hosted Ubuntu"
}

variable "project_group_name" {
  type    = string
  default = "Default Project Group"
}

variable "reconcile_enabled" {
  description = "Whether the trigger \"Reconcile hosts every 5 minutes\" runs. Off while the gateway's octopus runtime is disabled."
  type        = bool
  default     = false
}

variable "gateway_url" {
  description = "Base URL of the tdd gateway the workers connect to (FWORKER_GATEWAY_URL)."
  type        = string
  default     = ""
}

variable "image" {
  type    = string
  default = "ghcr.io/clearmeasure-aisf-sample-apps/fworker-host:latest"
}

variable "ghcr_username" {
  description = "GHCR user for the feed and Fworker.GhcrUsername (a machine user or the bot; not a secret); null leaves the feed anonymous until set in the portal."
  type        = string
  default     = null
}

variable "ghcr_token" {
  description = "A GitHub token with read:packages for the feed; null leaves it unset."
  type        = string
  default     = null
  sensitive   = true
}

data "octopusdeploy_environments" "env" {
  partial_name = var.environment_name
  take         = 10
}

data "octopusdeploy_worker_pools" "hosted" {
  partial_name = var.hosted_worker_pool_name
  take         = 10
}

data "octopusdeploy_lifecycles" "default" {
  partial_name = "Default Lifecycle"
  take         = 1
}

data "octopusdeploy_project_groups" "group" {
  partial_name = var.project_group_name
  take         = 1
}

locals {
  environment_id = one([for e in data.octopusdeploy_environments.env.environments : e.id if e.name == var.environment_name])
  hosted_pool_id = one([for p in data.octopusdeploy_worker_pools.hosted.worker_pools : p.id if p.name == var.hosted_worker_pool_name])

  script_on_targets = {
    "Octopus.Action.RunOnServer"         = "False"
    "Octopus.Action.Script.ScriptSource" = "Inline"
    "Octopus.Action.Script.Syntax"       = "Bash"
  }
  script_on_worker = {
    "Octopus.Action.RunOnServer"         = "True"
    "Octopus.Action.Script.ScriptSource" = "Inline"
    "Octopus.Action.Script.Syntax"       = "Bash"
  }

  # prompted at each run by the gateway (Octopus prompted variables)
  prompted = {
    "Fworker.Name"         = "Worker name; container fw-<name> (^[a-z0-9-]{1,40}$)"
    "Fworker.Item"         = "Work item number"
    "Fworker.Phase"        = "Board phase of the item"
    "Fworker.ModelProfile" = "Model profile the gateway chose"
    "Fworker.Nonce"        = "One-time nonce the worker redeems at the gateway"
  }

  # String settings with defaults (overridable per environment in the portal)
  settings = {
    "Fworker.GatewayUrl"          = var.gateway_url
    "Fworker.Image"               = var.image
    "Fworker.Cpus"                = "2"
    "Fworker.Memory"              = "8g"
    "Fworker.StartTimeoutSeconds" = "180"
    "Fworker.GhcrUsername"        = var.ghcr_username == null ? "" : var.ghcr_username
  }

  # Sensitive variables of the project, added outside Terraform (see the top): the name and what the value is
  sensitive_names = {
    "Fworker.AnthropicApiKey" = "Anthropic API key for FWRUNNER_AGENT=claude"
    "Fworker.CursorApiKey"    = "Cursor API key for FWRUNNER_AGENT=cursor"
    "Fworker.OpenAiApiKey"    = "OpenAI API key for FWRUNNER_AGENT=codex"
    "Fworker.HostToken"       = "Bearer token of the hosts at the gateway's /api/v1/runtime/octopus/* endpoints"
    "Fworker.GhcrToken"       = "GitHub token with read:packages, for docker login ghcr.io on the hosts"
  }
}

resource "octopusdeploy_project" "runtime" {
  name             = "cmdemo1-fworker-runtime"
  description      = "Starts and stops factory worker containers on self-hosted fworker-host targets (cmdemo1-fworker deploy/octopus)."
  lifecycle_id     = data.octopusdeploy_lifecycles.default.lifecycles[0].id
  project_group_id = data.octopusdeploy_project_groups.group.project_groups[0].id
}

# GHCR (private) as a feed of the space
resource "octopusdeploy_docker_container_registry" "ghcr" {
  name          = "GHCR clearmeasure-aisf-sample-apps"
  feed_uri      = "https://ghcr.io"
  api_version   = "v2"
  registry_path = "clearmeasure-aisf-sample-apps/fworker-host"
  username      = var.ghcr_username
  password      = var.ghcr_token
}

# --- runbooks -------------------------------------------------------------------------------------------------------------------------

resource "octopusdeploy_runbook" "start" {
  project_id                  = octopusdeploy_project.runtime.id
  name                        = "Start worker"
  description                 = "docker run -d of fw-<Fworker.Name> on the chosen fworker-host target; exits when the worker registered at the gateway."
  environment_scope           = "Specified"
  environments                = [local.environment_id]
  default_guided_failure_mode = "Off"
  connectivity_policy {
    allow_deployments_to_no_targets = false
    target_roles                    = [var.target_tag]
  }
}

resource "octopusdeploy_runbook" "start_overflow" {
  project_id                  = octopusdeploy_project.runtime.id
  name                        = "Start worker (overflow)"
  description                 = "Held run of fw-<Fworker.Name> on the Hosted Ubuntu dynamic pool for short roles; the task ends with the container (the gateway caps the duration)."
  environment_scope           = "Specified"
  environments                = [local.environment_id]
  default_guided_failure_mode = "Off"
}

resource "octopusdeploy_runbook" "stop" {
  project_id                  = octopusdeploy_project.runtime.id
  name                        = "Stop worker"
  description                 = "docker rm -f fw-<Fworker.Name>; idempotent."
  environment_scope           = "Specified"
  environments                = [local.environment_id]
  default_guided_failure_mode = "Off"
  connectivity_policy {
    allow_deployments_to_no_targets = true
    target_roles                    = [var.target_tag]
  }
}

resource "octopusdeploy_runbook" "reconcile" {
  project_id                  = octopusdeploy_project.runtime.id
  name                        = "Reconcile hosts"
  description                 = "Every 5 minutes on each fworker-host target: remove fw-* containers the gateway no longer wants or that exited, report the rest."
  environment_scope           = "Specified"
  environments                = [local.environment_id]
  default_guided_failure_mode = "Off"
  connectivity_policy {
    allow_deployments_to_no_targets = true
    exclude_unhealthy_targets       = true
    skip_machine_behavior           = "SkipUnavailableMachines"
    target_roles                    = [var.target_tag]
  }
}

resource "octopusdeploy_process" "start" {
  project_id = octopusdeploy_project.runtime.id
  runbook_id = octopusdeploy_runbook.start.id
}

resource "octopusdeploy_process" "start_overflow" {
  project_id = octopusdeploy_project.runtime.id
  runbook_id = octopusdeploy_runbook.start_overflow.id
}

resource "octopusdeploy_process" "stop" {
  project_id = octopusdeploy_project.runtime.id
  runbook_id = octopusdeploy_runbook.stop.id
}

resource "octopusdeploy_process" "reconcile" {
  project_id = octopusdeploy_project.runtime.id
  runbook_id = octopusdeploy_runbook.reconcile.id
}

resource "octopusdeploy_process_step" "start" {
  process_id           = octopusdeploy_process.start.id
  name                 = "Start worker container"
  type                 = "Octopus.Script"
  properties           = { "Octopus.Action.TargetRoles" = var.target_tag }
  execution_properties = merge(local.script_on_targets, { "Octopus.Action.Script.ScriptBody" = file("${path.module}/scripts/start-worker.sh") })
}

# overflow: on the Hosted Ubuntu dynamic pool, no execution container (the worker image needs the pool's own Docker), held mode forced
resource "octopusdeploy_process_step" "start_overflow" {
  process_id     = octopusdeploy_process.start_overflow.id
  name           = "Run worker container (held)"
  type           = "Octopus.Script"
  worker_pool_id = local.hosted_pool_id
  execution_properties = merge(local.script_on_worker, {
    "Octopus.Action.Script.ScriptBody" = "export FWORKER_MODE_FORCE=held\n${file("${path.module}/scripts/start-worker.sh")}"
  })
}

resource "octopusdeploy_process_step" "stop" {
  process_id           = octopusdeploy_process.stop.id
  name                 = "Remove worker container"
  type                 = "Octopus.Script"
  properties           = { "Octopus.Action.TargetRoles" = var.target_tag }
  execution_properties = merge(local.script_on_targets, { "Octopus.Action.Script.ScriptBody" = file("${path.module}/scripts/stop-worker.sh") })
}

resource "octopusdeploy_process_step" "reconcile" {
  process_id           = octopusdeploy_process.reconcile.id
  name                 = "Reconcile fw-* containers"
  type                 = "Octopus.Script"
  properties           = { "Octopus.Action.TargetRoles" = var.target_tag }
  execution_properties = merge(local.script_on_targets, { "Octopus.Action.Script.ScriptBody" = file("${path.module}/scripts/reconcile-hosts.sh") })
}

resource "octopusdeploy_project_scheduled_trigger" "reconcile" {
  name        = "Reconcile hosts every 5 minutes"
  description = "Runs the runbook Reconcile hosts in tdd on every fworker-host target."
  project_id  = octopusdeploy_project.runtime.id
  space_id    = var.space_id
  timezone    = "UTC"
  is_disabled = !var.reconcile_enabled
  run_runbook_action {
    runbook_id             = octopusdeploy_runbook.reconcile.id
    target_environment_ids = [local.environment_id]
  }
  cron_expression_schedule {
    cron_expression = "0 */5 * * * *"
  }
}

# --- variables ------------------------------------------------------------------------------------------------------------------------

resource "octopusdeploy_variable" "prompted" {
  for_each = local.prompted
  owner_id = octopusdeploy_project.runtime.id
  type     = "String"
  name     = each.key
  value    = ""
  prompt {
    label       = each.key
    description = each.value
    # never required and not scoped (Octopus offers no prompt scoped to a runbook process): a scheduled Reconcile hosts run sends no values,
    # Stop worker sends no nonce; the scripts validate what they use
    is_required = false
    display_settings {
      control_type = "SingleLineText"
    }
  }
}

resource "octopusdeploy_variable" "agent" {
  owner_id = octopusdeploy_project.runtime.id
  type     = "String"
  name     = "Fworker.Agent"
  value    = var.default_agent
  prompt {
    label       = "Fworker.Agent"
    description = "Coding CLI of the worker"
    is_required = false
    display_settings {
      control_type = "Select"
      select_option {
        display_name = "Claude Code"
        value        = "claude"
      }
      select_option {
        display_name = "Cursor agent"
        value        = "cursor"
      }
      select_option {
        display_name = "Codex"
        value        = "codex"
      }
    }
  }
}

resource "octopusdeploy_variable" "mode" {
  owner_id = octopusdeploy_project.runtime.id
  type     = "String"
  name     = "Fworker.Mode"
  value    = "detached"
  prompt {
    label       = "Fworker.Mode"
    description = "detached: start and exit; held: the task runs as long as the container"
    is_required = false
    display_settings {
      control_type = "Select"
      select_option {
        display_name = "Detached"
        value        = "detached"
      }
      select_option {
        display_name = "Held"
        value        = "held"
      }
    }
  }
}

resource "octopusdeploy_variable" "setting" {
  for_each = local.settings
  owner_id = octopusdeploy_project.runtime.id
  type     = "String"
  name     = each.key
  value    = each.value
}

# --- the gateway's identity: a service account with run/view of these runbooks only ----------------------------------------------------

# The first gateway account (Standard service account, Users-903): Octopus refused its runbook runs with "Missing permission:
# RunbookRunCreate" even with the permission granted space-wide (2026-10-23). Kept, without a key and out of the team.
resource "octopusdeploy_user" "gateway" {
  username     = "cmdemo1-fworker-gateway"
  display_name = "cmdemo1 fworker gateway (runtime adapter)"
  is_service   = true
  is_active    = true
}

# The gateway's account (Users-904): a service account of type "Agent", whose runbook runs Octopus accepts. Provider 1.21 has no attribute
# for the type: it was set when the account was created and is imported here (terraform import octopusdeploy_user.gateway_agent Users-904).
# Its API key (purpose "fworker-gateway-runbooks (Agent)", expires 2027-04-22) is in tdd's vault as fworker-octopus-runbook-api-key; Octopus
# lets only a person mint it, so rotating it before it expires is a person's job.
resource "octopusdeploy_user" "gateway_agent" {
  username     = "cmdemo1-fworker-gateway-agent"
  display_name = "cmdemo1 fworker gateway (Agent)"
  is_service   = true
  is_active    = true
}

resource "octopusdeploy_user_role" "runbook_runner" {
  name                       = "fworker runbook runner"
  description                = "Run and view runbooks, view tasks (the space permissions of the built-in Runbook consumer)."
  granted_space_permissions  = ["RunbookRunCreate", "RunbookRunView", "RunbookView", "TaskView", "ProjectView", "EnvironmentView", "MachineView", "FeedView", "LibraryVariableSetView", "ArtifactView", "EventView", "InterruptionView", "ProjectGroupView", "TriggerView", "TenantView", "CertificateView", "MachinePolicyView", "TeamView"]
  granted_system_permissions = []
}

resource "octopusdeploy_team" "gateway" {
  name        = "cmdemo1-fworker-runtime gateway"
  description = "The gateway's service account: runs the runbooks of cmdemo1-fworker-runtime in tdd."
  space_id    = var.space_id
  users       = [octopusdeploy_user.gateway_agent.id]
  user_role {
    user_role_id = octopusdeploy_user_role.runbook_runner.id
    space_id     = var.space_id
    # the runtime project in tdd only. The 403s of 2026-10-23 were the Standard account type, not this scope: with the Agent account the
    # narrowed scope was tested and accepted (Stop worker ServerTasks-12099143, Start worker ServerTasks-12099156)
    project_ids     = [octopusdeploy_project.runtime.id]
    environment_ids = [local.environment_id]
  }
}

output "project_id" { value = octopusdeploy_project.runtime.id }
output "runbook_ids" {
  value = {
    "Start worker"            = octopusdeploy_runbook.start.id
    "Start worker (overflow)" = octopusdeploy_runbook.start_overflow.id
    "Stop worker"             = octopusdeploy_runbook.stop.id
    "Reconcile hosts"         = octopusdeploy_runbook.reconcile.id
  }
}
output "gateway_service_account_id" { value = octopusdeploy_user.gateway.id }
output "sensitive_variables_to_add" { value = keys(local.sensitive_names) }
