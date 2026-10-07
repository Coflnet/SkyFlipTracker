VERSION=0.16.4
PACKAGE_NAME=Coflnet.Sky.FlipTracker.Client
SERVICE_FLAGS="$(cd "$(dirname "$0")" && pwd)/../Models/FlipFlags.cs"

# Prints Name=Value for every FlipFlags member the service declares with a literal value
declared_flip_flags() {
    sed -nE 's/^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*([0-9]+)\s*,?\s*$/\1=\2/p' "$SERVICE_FLAGS"
}

# The generator numbers enum members 1, 2, 3, ... so every member is set to the value the
# service declares for it; a member only one side knows stops the release instead
patch_flip_flags() {
    local generated=$1 member
    local declared=$(declared_flip_flags)
    local generated_names=$(sed -nE 's/.*EnumMember\(Value = "([^"]+)"\).*/\1/p' "$generated" | sort)
    if [ -z "$declared" ] || [ "$(echo "$declared" | cut -d= -f1 | sort)" != "$generated_names" ]; then
        echo "FlipFlags members of $SERVICE_FLAGS and of the generated client differ, generated:" $generated_names >&2
        return 1
    fi
    for member in $declared; do
        sed -i -E "/EnumMember\(Value = \"${member%%=*}\"\)/{n;s/= [0-9]+/= ${member#*=}/}" "$generated"
    done
    sed -i 's/))]/))]\n    [Flags]/g' "$generated"
}

docker run --rm -v "${PWD}:/local" --network host -u $(id -u ${USER}):$(id -g ${USER})  openapitools/openapi-generator-cli generate \
-i http://localhost:5017/api/openapi/v1/openapi.json \
-g csharp \
-o /local/out --additional-properties=packageName=$PACKAGE_NAME,packageVersion=$VERSION,licenseId=MIT,targetFramework=net8.0,library=restsharp

cd out
path=src/$PACKAGE_NAME/$PACKAGE_NAME.csproj
sed -i 's/GIT_USER_ID/Coflnet/g' $path
sed -i 's/GIT_REPO_ID/SkyFlipTracker/g' $path
sed -i 's/>OpenAPI/>Coflnet/g' $path

patch_flip_flags src/$PACKAGE_NAME/Model/FlipFlags.cs || exit 1

sed -i 's@annotations</Nullable>@annotations</Nullable>\n    <PackageReadmeFile>README.md</PackageReadmeFile>@g' $path
sed -i '34i    <None Include="../../../../README.md" Pack="true" PackagePath="\"/>' $path

dotnet pack
cp src/$PACKAGE_NAME/bin/Release/$PACKAGE_NAME.*.nupkg ..
dotnet nuget push ../$PACKAGE_NAME.$VERSION.nupkg --api-key $NUGET_API_KEY --source "nuget.org" --skip-duplicate