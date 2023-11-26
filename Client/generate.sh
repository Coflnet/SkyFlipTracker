VERSION=0.9.1

docker run --rm -v "${PWD}:/local" --network host -u $(id -u ${USER}):$(id -g ${USER})  openapitools/openapi-generator-cli generate \
-i http://localhost:5017/swagger/v1/swagger.json \
-g csharp \
-o /local/out --additional-properties=packageName=Coflnet.Sky.FlipTracker.Client,packageVersion=$VERSION,licenseId=MIT

cd out
sed -i 's/GIT_USER_ID/Coflnet/g' src/Coflnet.Sky.FlipTracker.Client/Coflnet.Sky.FlipTracker.Client.csproj
sed -i 's/GIT_REPO_ID/SkyFlipTracker/g' src/Coflnet.Sky.FlipTracker.Client/Coflnet.Sky.FlipTracker.Client.csproj
sed -i 's/>OpenAPI/>Coflnet/g' src/Coflnet.Sky.FlipTracker.Client/Coflnet.Sky.FlipTracker.Client.csproj

dotnet pack
cp src/Coflnet.Sky.FlipTracker.Client/bin/Debug/Coflnet.Sky.FlipTracker.Client.*.nupkg ..
