VERSION=0.14.4
PACKAGE_NAME=Coflnet.Sky.FlipTracker.Client

docker run --rm -v "${PWD}:/local" --network host -u $(id -u ${USER}):$(id -g ${USER})  openapitools/openapi-generator-cli generate \
-i http://localhost:5017/api/openapi/v1/openapi.json \
-g csharp \
-o /local/out --additional-properties=packageName=$PACKAGE_NAME,packageVersion=$VERSION,licenseId=MIT,targetFramework=net6.0

cd out
path=src/$PACKAGE_NAME/$PACKAGE_NAME.csproj
sed -i 's/GIT_USER_ID/Coflnet/g' $path
sed -i 's/GIT_REPO_ID/SkyFlipTracker/g' $path
sed -i 's/>OpenAPI/>Coflnet/g' $path

sed -i 's/None = 1/None = 0/g' src/$PACKAGE_NAME/Model/FlipFlags.cs
sed -i 's/DifferentBuyer = 2/DifferentBuyer = 1/g' src/$PACKAGE_NAME/Model/FlipFlags.cs
sed -i 's/ViaTrade = 3/ViaTrade = 2/g' src/$PACKAGE_NAME/Model/FlipFlags.cs
sed -i 's/))]/))]\n    [Flags]/g' src/$PACKAGE_NAME/Model/FlipFlags.cs

sed -i 's@annotations</Nullable>@annotations</Nullable>\n    <PackageReadmeFile>README.md</PackageReadmeFile>@g' $path
sed -i '34i    <None Include="../../../../README.md" Pack="true" PackagePath="\"/>' $path

dotnet pack
cp src/$PACKAGE_NAME/bin/Release/$PACKAGE_NAME.*.nupkg ..
dotnet nuget push ../$PACKAGE_NAME.$VERSION.nupkg --api-key $NUGET_API_KEY --source "nuget.org" --skip-duplicate