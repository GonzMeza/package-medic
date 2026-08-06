export const simulationJsonExample = `{
  "schemaVersion": 2,
  "kind": "dependencySimulation",
  "toolVersion": "0.6.0",
  "repository": {
    "headCommit": "0000000000000000000000000000000000000000",
    "analysisTarget": "MySolution.sln",
    "workingTreeRequiredClean": true
  },
  "request": { "packageId": "Example.Package", "candidateVersion": "2.0.0" },
  "mutation": {
    "packageId": "Example.Package",
    "file": "Directory.Packages.props",
    "line": 18,
    "kind": "centralPackageVersion",
    "beforeVersion": "1.5.0",
    "candidateVersion": "2.0.0",
    "affectedProjects": ["src/App/App.csproj"],
    "noChange": false,
    "sourceSha256Before": "1111111111111111111111111111111111111111111111111111111111111111",
    "sourceSha256After": "2222222222222222222222222222222222222222222222222222222222222222"
  },
  "verification": {
    "restore": "passed",
    "build": "notRun",
    "tests": "notRun",
    "runtimeCompatibility": "notVerified",
    "evidenceLevel": "restoreOnly",
    "auditedVulnerabilities": false,
    "auditedDeprecations": false,
    "lockedMode": "notEnabled"
  },
  "comparison": {
    "diagnosticSummary": { "added": 0, "resolved": 0, "severityChanged": 0 },
    "diagnosticChanges": [],
    "packageSummary": {
      "added": 0, "removed": 0, "upgraded": 0, "downgraded": 0,
      "uncomparableVersionChanges": 0, "directToTransitive": 0,
      "transitiveToDirect": 0, "otherModified": 0
    },
    "packageChanges": [],
    "riskSummary": {
      "vulnerabilitiesIntroduced": 0, "vulnerabilitiesResolved": 0,
      "deprecationsIntroduced": 0, "deprecationsResolved": 0,
      "vulnerabilitiesPersistent": 0, "deprecationsPersistent": 0
    },
    "projectSettingsChanges": [],
    "isComplete": true
  },
  "isComplete": true,
  "verdict": "pass",
  "rejectionReasons": [],
  "errors": []
}`;
