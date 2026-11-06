# The argument check of the verbs: a missing or empty argument is a usage error.

function Need($a, [string]$k) { if (-not (Test-Has $a $k) -or "$($a[$k])" -eq '') { Reject 2 'usage' "missing argument: $k" }; $a[$k] }
