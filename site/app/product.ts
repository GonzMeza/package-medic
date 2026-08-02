export const product = {
  name: "PackageMedic",
  packageId: "PackageMedic.Tool",
  version: "0.1.0-preview.1",
  command: "package-medic",
} as const;

export const installCommand =
  `dotnet tool install --global ${product.packageId} --version ${product.version}`;

export const doctorCommand = `${product.command} doctor`;
export const nugetUrl = `https://www.nuget.org/packages/${product.packageId}`;
