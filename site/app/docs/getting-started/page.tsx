import { installCommand, product } from "../../product";
import CodeBlock from "../code-block";
import { Callout, DocPage, OptionTable, PageLinks } from "../components";

export default function GettingStartedPage() {
  return (
    <DocPage
      eyebrow="Fundamentals"
      title="Install and run the first diagnosis."
      description="PackageMedic is a cross-platform global .NET tool. A first scan takes one install command and one repository path."
    >
      <section id="requirements">
        <h2>Requirements</h2>
        <ul>
          <li>.NET 8 or a newer compatible runtime.</li>
          <li>A .NET SDK capable of loading the SDK-style projects being analyzed.</li>
          <li>Projects targeting .NET 8, 9, or 10 are supported when their required SDK is installed.</li>
        </ul>
      </section>

      <section id="install">
        <h2>Install the stable tool</h2>
        <CodeBlock>{installCommand}</CodeBlock>
        <p>To update an existing global installation:</p>
        <CodeBlock>{"dotnet tool update --global PackageMedic.Tool"}</CodeBlock>
        <p>Confirm the executable and documentation version match:</p>
        <CodeBlock>{"package-medic --version"}</CodeBlock>
      </section>

      <section id="first-scan">
        <h2>Run the first scan</h2>
        <CodeBlock>{`# Current directory
package-medic doctor

# One project
package-medic doctor ./src/MyProject/MyProject.csproj

# A solution or solution XML file
package-medic doctor ./MySolution.sln
package-medic doctor ./MySolution.slnx

# Every discoverable project under a directory
package-medic doctor ./src`}</CodeBlock>
        <Callout title="Restore is enabled by default">
          <p>
            <code>doctor</code> runs <code>dotnet restore</code>, which can contact feeds from your
            NuGet configuration. Use <code>--no-restore</code> only when every selected project has
            a usable <code>obj/project.assets.json</code>.
          </p>
        </Callout>
      </section>

      <section id="read-result">
        <h2>Read the result</h2>
        <p>
          Each diagnostic shows a stable PM code, severity, explanation, evidence, affected
          project or scope, source location when available, suggested action, and confidence.
        </p>
        <OptionTable
          headers={["Exit", "Meaning", "CI interpretation"]}
          rows={[
            [<code key="0">0</code>, "The scan completed below the selected threshold.", "Pass"],
            [<code key="1">1</code>, "At least one effective diagnostic reached the threshold.", "Policy failure"],
            [<code key="2">2</code>, "Usage, configuration, restore, audit, or analysis failed.", "Operational failure"],
          ]}
        />
        <p>
          Version {product.version} defaults to failing on warnings. To explore a repository
          without failing the command, use <code>--fail-on none</code>.
        </p>
        <CodeBlock>{"package-medic doctor . --fail-on none --verbosity detailed"}</CodeBlock>
      </section>

      <section id="next">
        <h2>Recommended next steps</h2>
        <ol>
          <li>Review every current finding and confirm the evidence.</li>
          <li>Run <code>package-medic init</code> to create repository policy.</li>
          <li>Create a baseline only after accepted findings have been reviewed.</li>
          <li>Add the GitHub Action with a new-only failure gate.</li>
        </ol>
      </section>

      <PageLinks
        previous={{ href: "/docs", label: "Overview" }}
        next={{ href: "/docs/commands", label: "Commands" }}
      />
    </DocPage>
  );
}
