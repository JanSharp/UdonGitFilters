
(This is just bare minimum and bad documentation of what this program is doing that I've thrown together real quick.)

# UdonGitFilters

This program reduces the amount of merge conflicts caused by Udon and UdonSharp. It works on the assumption that the `SerializedUdonPrograms` folder (`[Aa]ssets/[Ss]erialized[Uu]don[Pp]rograms`) is in `.gitignore`, which it should be because that contains purely generated files. The filters applied to scene, prefab and asset files then unset references to these generated files (such that they are not stored in git, but them existing in a local file does not cause the file to be considered modified).

For udon sharp asset files they get completely replaced with clean/empty/new udon sharp asset files inside of git, since the content of these files is also purely generated aside from the name and cs file reference, and they must be included in git for their meta file to keep references to them in tact.

For udon graph asset, scene and prefab files the references to the serialized udon program asset file get unset.

Unity files get compressed by running them through `7z` (seven zip) using the `xz` compression format.

## Limitation

There is currently literally no way to choose what should and shouldn't get compressed, it's just hard coded that `.unity` files get compressed.

Do **not** use git lfs filter extensions for this program because the changes it makes in the `clean` filter won't be undone in the `smudge` filter, causing git lfs to fail the checksum validation after performing a `smudge`, ultimately preventing any checkout of files ran through this program.

## Seven Zip

Seven zip must be installed an the `7z` executable must be in a folder that is included in the PATH environment variable. The `xz` compression format, which uses `LZMA2` compression method, must be supported.

## Dotnet

The dotnet 8.0 sdk and runtime must be installed. Run `dotnet sdk check` to see what is installed.

## Usage

```shell
git clone https://github.com/JanSharp/UdonGitFilters.git
cd UdonGitFilters
dotnet restore
dotnet publish
```

Copy the path of the executable and use that full path in the git config file or put the folder path into the PATH environment variable and just use `UdonGitFilters` in the git config file.

On Linux you can also just create a symlink in `/usr/local/bin` to the `UdonGitFilters` executable, for example: `sudo ln -s /mnt/big/dev/UdonGitFilters/bin/Release/net8.0/UdonGitFilters /usr/local/bin/UdonGitFilters`

Example full executable path to use when PATH was not set: `/mnt/big/dev/UdonGitFilters/bin/Release/net8.0/UdonGitFilters`\
Example folder path to put into PATH: `/mnt/big/dev/UdonGitFilters/bin/Release/net8.0`\
Example executable name to use if PATH was set: `UdonGitFilters`

Run `git config --global -e` (I hope you've got a usable text editor defined for git)

The following are supported by this program:

```
[filter "unityasset"] # For .asset files, specifically detecting and handling udon graph and udon sharp asset files.
  clean = UdonGitFilters clean %f
  smudge = UdonGitFilters smudge %f
	required = true
[filter "unityscene"] # For .unity files, setting serializedProgramAsset references to none and rinning it through 7z using xz compression format.
  clean = UdonGitFilters clean %f | git-lfs clean -- %f
  smudge = git-lfs smudge -- %f | UdonGitFilters smudge %f
	required = true
[filter "unityprefab"] # For .prefab files, setting serializedProgramAsset references to none.
  clean = UdonGitFilters clean %f | git-lfs clean -- %f
  smudge = git-lfs smudge -- %f | UdonGitFilters smudge %f
	required = true
```

And then in `.gitattributes` for example:

```
*.unity filter=unityscene diff=lfs merge=lfs -text
*.prefab filter=unityprefab diff=lfs merge=lfs -text
*.asset filter=unityasset merge=unityyamlmerge -text
```

And then any other `.asset` files that need to be in git lfs should be defined after these lines in order for those to override this filter. I think that's how that works, I've not actually confirmed it 100%.

And since I've mentioned `unityyamlmerge` here, look at this if you'd like: https://docs.unity3d.com/Manual/SmartMerge.html

Though I'll be honest, whenever I've personally had to merge scene or prefab files using this tool, while it resolves the merge conflicts, the resulting scenes or prefabs have hardly been usable for me. It might work for some very minor merges, but any substantial ones and it'll likely either break the prefab by having "2 root objects", or make it so hard to understand what exactly needs manual attention and how that it's easier to just redo the changes that were attempted to be merged manually.
