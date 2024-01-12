VERSION=0.10.0

docker run --rm -v "${PWD}:/local" --network host -u $(id -u ${USER}):$(id -g ${USER})  openapitools/openapi-generator-cli generate \
-i http://localhost:5017/swagger/v1/swagger.json \
-g csharp \
-o /local/out --additional-properties=packageName=Coflnet.Sky.FlipTracker.Client,packageVersion=$VERSION,licenseId=MIT

cd out
path=src/Coflnet.Sky.FlipTracker.Client/Coflnet.Sky.FlipTracker.Client.csproj
sed -i 's/GIT_USER_ID/Coflnet/g' $path
sed -i 's/GIT_REPO_ID/SkyFlipTracker/g' $path
sed -i 's/>OpenAPI/>Coflnet/g' $path

sed -i 's/None = 1/None = 0/g' src/Coflnet.Sky.FlipTracker.Client/Model/FlipFlags.cs
sed -i 's/DifferentBuyer = 2/DifferentBuyer = 1/g' src/Coflnet.Sky.FlipTracker.Client/Model/FlipFlags.cs
sed -i 's/ViaTrade = 3/ViaTrade = 2/g' src/Coflnet.Sky.FlipTracker.Client/Model/FlipFlags.cs
sed -i 's/))]/))]\n    [Flags]/g' src/Coflnet.Sky.FlipTracker.Client/Model/FlipFlags.cs

sed -i 's@annotations</Nullable>@annotations</Nullable>\n    <PackageReadmeFile>README.md</PackageReadmeFile>@g' $path
sed -i 's@Remove="System.Web" />@Remove="System.Web" />\n    <None Include="../../../../README.md" Pack="true" PackagePath="\"/>@g' $path

dotnet pack
cp src/Coflnet.Sky.FlipTracker.Client/bin/Release/Coflnet.Sky.FlipTracker.Client.*.nupkg ..
