VERSION=0.11.1

docker run --rm -v "${PWD}:/local" --network host -u $(id -u ${USER}):$(id -g ${USER})  openapitools/openapi-generator-cli generate \
-i http://localhost:5017/swagger/v1/swagger.json \
-g csharp \
-o /local/out --additional-properties=packageName=Coflnet.Sky.FlipTracker.Client,packageVersion=$VERSION,licenseId=MIT,targetFramework=net6.0

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
sed -i '34i    <None Include="../../../../README.md" Pack="true" PackagePath="\"/>' $path

dotnet pack
cp src/Coflnet.Sky.FlipTracker.Client/bin/Release/Coflnet.Sky.FlipTracker.Client.*.nupkg ..
