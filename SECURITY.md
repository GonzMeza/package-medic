# Security policy

PackageMedic is experimental and has not yet published a supported release line.

Please do not open public issues for suspected vulnerabilities or accidentally exposed credentials. Use GitHub's private vulnerability reporting for `GonzMeza/package-medic` when available. Include affected versions, reproduction steps, impact, and any proposed mitigation. Do not include live feed tokens or passwords; replace them with inert placeholders.

PackageMedic does not collect telemetry. Its own analyzer does not call remote services, but the default `doctor` workflow runs `dotnet restore`, which may contact feeds configured by the user. Use `--no-restore` when network access is not acceptable.

Only the latest source revision receives security fixes during the experimental phase.
