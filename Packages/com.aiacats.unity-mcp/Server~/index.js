#!/usr/bin/env node

import { Server } from '@modelcontextprotocol/sdk/server/index.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { CallToolRequestSchema, ListResourcesRequestSchema, ListToolsRequestSchema, ReadResourceRequestSchema } from '@modelcontextprotocol/sdk/types.js';

/**
 * Claude Code MCP Unity Server
 * Connects to Unity Editor via HTTP REST API
 */
class ClaudeCodeMCPUnityServer {
  constructor() {
    this.unityUrl = process.env.MCP_UNITY_HTTP_URL || 'http://localhost:8090';
    this.server = new Server(
      {
        name: 'claude-code-mcp-unity',
        version: '1.0.0',
      },
      {
        capabilities: {
          resources: {},
          tools: {},
        },
      }
    );

    this.setupHandlers();
  }

  setupHandlers() {
    // List available tools
    this.server.setRequestHandler(ListToolsRequestSchema, async () => {
      return {
        tools: [
          {
            name: 'execute_menu_item',
            description: 'Executes a Unity menu item by path',
            inputSchema: {
              type: 'object',
              properties: {
                menuPath: {
                  type: 'string',
                  description: 'The path to the menu item to execute (e.g. "GameObject/Create Empty")'
                }
              },
              required: ['menuPath']
            }
          },
          {
            name: 'select_gameobject',
            description: 'Sets the selected GameObject in the Unity editor by path or instance ID',
            inputSchema: {
              type: 'object',
              properties: {
                objectPath: {
                  type: 'string',
                  description: 'The path or name of the GameObject to select (e.g. "Main Camera")'
                },
                instanceId: {
                  type: 'number',
                  description: 'The instance ID of the GameObject to select'
                }
              }
            }
          },
          {
            name: 'update_gameobject',
            description: 'Updates properties of a GameObject in the Unity scene by its instance ID or path. If the GameObject does not exist at the specified path, it will be created.',
            inputSchema: {
              type: 'object',
              properties: {
                objectPath: {
                  type: 'string',
                  description: 'The path of the GameObject in the hierarchy to update (alternative to instanceId)'
                },
                instanceId: {
                  type: 'number',
                  description: 'The instance ID of the GameObject to update'
                },
                gameObjectData: {
                  type: 'object',
                  description: 'An object containing the fields to update on the GameObject. If the GameObject does not exist at objectPath, it will be created.',
                  properties: {
                    name: {
                      type: 'string',
                      description: 'New name for the GameObject'
                    },
                    activeSelf: {
                      type: 'boolean',
                      description: 'Set the active state of the GameObject (GameObject.SetActive(value))'
                    },
                    tag: {
                      type: 'string',
                      description: 'New tag for the GameObject'
                    },
                    layer: {
                      type: 'integer',
                      description: 'New layer for the GameObject'
                    },
                    isStatic: {
                      type: 'boolean',
                      description: 'Set the static state of the GameObject (GameObject.isStatic = value)'
                    }
                  }
                }
              },
              required: ['gameObjectData']
            }
          },
          {
            name: 'update_component',
            description: 'Updates component fields on a GameObject or adds it to the GameObject if it does not contain the component',
            inputSchema: {
              type: 'object',
              properties: {
                objectPath: {
                  type: 'string',
                  description: 'The path of the GameObject in the hierarchy to update (alternative to instanceId)'
                },
                instanceId: {
                  type: 'number',
                  description: 'The instance ID of the GameObject to update'
                },
                componentName: {
                  type: 'string',
                  description: 'The name of the component to update or add'
                },
                componentData: {
                  type: 'object',
                  description: 'An object containing the fields to update on the component (optional)'
                }
              },
              required: ['componentName']
            }
          },
          {
            name: 'send_console_log',
            description: 'Sends console log messages to the Unity console',
            inputSchema: {
              type: 'object',
              properties: {
                message: {
                  type: 'string',
                  description: 'The message to display in the Unity console'
                },
                type: {
                  type: 'string',
                  description: 'The type of message (info, warning, error) - defaults to info (optional)'
                }
              },
              required: ['message']
            }
          },
          {
            name: 'get_console_logs',
            description: 'Retrieves logs from the Unity console with pagination support to avoid token limits',
            inputSchema: {
              type: 'object',
              properties: {
                logType: {
                  type: 'string',
                  enum: ['info', 'warning', 'error'],
                  description: 'The type of logs to retrieve (info, warning, error) - defaults to all logs if not specified'
                },
                limit: {
                  type: 'integer',
                  minimum: 1,
                  maximum: 500,
                  description: 'Maximum number of logs to return (defaults to 50, max 500 to avoid token limits)'
                },
                offset: {
                  type: 'integer',
                  minimum: 0,
                  description: 'Starting index for pagination (0-based, defaults to 0)'
                },
                includeStackTrace: {
                  type: 'boolean',
                  description: 'Whether to include stack trace in logs. Always set to false to save 80-90% tokens, unless you specifically need stack traces for debugging. Default: true (except info logs in resource)'
                }
              }
            }
          },
          {
            name: 'add_package',
            description: 'Adds a package to the Unity Package Manager (UnityEditor.PackageManager.Client.Add, run on the main thread). Resolution is asynchronous; use wait_for_compilation_done or get_compilation_errors afterwards.',
            inputSchema: {
              type: 'object',
              properties: {
                source: {
                  type: 'string',
                  description: 'The source to use (registry, github, or disk) to add the package'
                },
                packageName: {
                  type: 'string',
                  description: 'The package name to add from Unity registry (e.g. com.unity.textmeshpro)'
                },
                repositoryUrl: {
                  type: 'string',
                  description: 'The GitHub repository URL (e.g. https://github.com/username/repo.git)'
                },
                path: {
                  type: 'string',
                  description: 'The path to use (folder path for disk method or subfolder for GitHub)'
                },
                branch: {
                  type: 'string',
                  description: 'The branch to use for GitHub packages (optional)'
                },
                version: {
                  type: 'string',
                  description: 'The version to use for registry packages (optional)'
                }
              },
              required: ['source']
            }
          },
          {
            name: 'remove_package',
            description: 'Removes a package from the Unity Package Manager (Client.Remove, main thread). Resolution is asynchronous.',
            inputSchema: {
              type: 'object',
              properties: {
                packageName: {
                  type: 'string',
                  description: 'The package name to remove (e.g. com.cysharp.r3)'
                }
              },
              required: ['packageName']
            }
          },
          {
            name: 'resolve_packages',
            description: 'Forces Unity to re-resolve packages from the manifest (Client.Resolve, main thread). Use after editing Packages/manifest.json externally so changes are picked up without deleting packages-lock.json or restarting.',
            inputSchema: {
              type: 'object',
              properties: {}
            }
          },
          {
            name: 'run_tests',
            description: 'Runs Unity Test Framework tests (EditMode or PlayMode). Tests run asynchronously - use queryOnly=true to poll for results after starting.',
            inputSchema: {
              type: 'object',
              properties: {
                testMode: {
                  type: 'string',
                  enum: ['EditMode', 'PlayMode'],
                  default: 'EditMode',
                  description: 'The test mode to run (EditMode or PlayMode). Default: EditMode'
                },
                testFilter: {
                  type: 'string',
                  description: 'Specific test name or class name to filter (must include namespace). Omit to run all tests.'
                },
                returnOnlyFailures: {
                  type: 'boolean',
                  default: true,
                  description: 'Only include failed tests in results. Default: true'
                },
                returnWithLogs: {
                  type: 'boolean',
                  default: false,
                  description: 'Include test output logs in results. Default: false'
                },
                queryOnly: {
                  type: 'boolean',
                  default: false,
                  description: 'If true, do not start new tests - only return the last test run results and status.'
                }
              }
            }
          },
          {
            name: 'add_asset_to_scene',
            description: 'Adds an asset from the AssetDatabase to the Unity scene',
            inputSchema: {
              type: 'object',
              properties: {
                assetPath: {
                  type: 'string',
                  description: 'The path of the asset in the AssetDatabase'
                },
                guid: {
                  type: 'string',
                  description: 'The GUID of the asset'
                },
                parentPath: {
                  type: 'string',
                  description: 'The path of the parent GameObject in the hierarchy'
                },
                parentId: {
                  type: 'number',
                  description: 'The instance ID of the parent GameObject'
                },
                position: {
                  type: 'object',
                  description: 'Position in the scene (defaults to Vector3.zero)',
                  properties: {
                    x: {
                      type: 'number',
                      default: 0,
                      description: 'X position in the scene'
                    },
                    y: {
                      type: 'number',
                      default: 0,
                      description: 'Y position in the scene'
                    },
                    z: {
                      type: 'number',
                      default: 0,
                      description: 'Z position in the scene'
                    }
                  }
                }
              }
            }
          },
          {
            name: 'hot_reload',
            description: 'Performs hot reload of Unity scripts and assets. This triggers script recompilation and asset refresh without stopping play mode if possible.',
            inputSchema: {
              type: 'object',
              properties: {
                saveAssets: {
                  type: 'boolean',
                  default: true,
                  description: 'Whether to save all modified assets before reloading (recommended: true)'
                },
                optimized: {
                  type: 'boolean',
                  default: true,
                  description: 'Whether to use optimized recompilation (scripts only) or full asset refresh (recommended: true for faster reload)'
                }
              }
            }
          },
          {
            name: 'force_compilation',
            description: 'Forces Unity to recompile all scripts and refresh all assets. More thorough than hot_reload but slower.',
            inputSchema: {
              type: 'object',
              properties: {
                forceUpdate: {
                  type: 'boolean',
                  default: true,
                  description: 'Whether to force update all assets during compilation (recommended: true)'
                }
              }
            }
          },
          {
            name: 'check_compilation_status',
            description: 'Checks the current compilation status of Unity, including whether it is compiling, playing, or ready.',
            inputSchema: {
              type: 'object',
              properties: {}
            }
          },
          {
            name: 'enter_play_mode',
            description: 'Enters Play Mode in the Unity Editor. Does not depend on window focus (uses EditorApplication.isPlaying on the main thread), so it works headless/unfocused. Refuses while compiling or already playing.',
            inputSchema: {
              type: 'object',
              properties: {}
            }
          },
          {
            name: 'exit_play_mode',
            description: 'Exits Play Mode in the Unity Editor (returns to Edit Mode). Focus-independent.',
            inputSchema: {
              type: 'object',
              properties: {}
            }
          },
          {
            name: 'get_play_state',
            description: 'Returns the current Editor play state (isPlaying, isPaused, isCompiling).',
            inputSchema: {
              type: 'object',
              properties: {}
            }
          },
          {
            name: 'restart_editor',
            description: 'Restarts the Unity Editor on the current project (EditorApplication.OpenProject). The MCP server drops and returns after Unity relaunches. Useful after manifest/package changes that need a clean reload.',
            inputSchema: {
              type: 'object',
              properties: {}
            }
          },
          {
            name: 'clear_console',
            description: 'Clears the Unity Editor console logs.',
            inputSchema: {
              type: 'object',
              properties: {}
            }
          },
          {
            name: 'set_game_view_display',
            description: 'Changes which Display the Game View renders (the "Display 1/2/..." dropdown). Useful when a UIDocument/PanelSettings or camera targets a specific display. display is 0-based (0 = Display 1).',
            inputSchema: {
              type: 'object',
              properties: {
                display: {
                  type: 'integer',
                  default: 0,
                  description: '0-based display index (0 = Display 1, 1 = Display 2, ...).'
                }
              }
            }
          },
          {
            name: 'wait_for_compilation_done',
            description: 'Blocks until Unity finishes the current compilation, then returns the result snapshot. Use this instead of polling check_compilation_status. If Unity is not currently compiling, waits up to graceMs for one to start (handles the race after force_compilation/hot_reload), then returns immediately if no compile begins.',
            inputSchema: {
              type: 'object',
              properties: {
                timeoutMs: {
                  type: 'number',
                  default: 60000,
                  description: 'Maximum time in milliseconds to wait for compilation to finish. Default 60000.'
                },
                graceMs: {
                  type: 'number',
                  default: 1500,
                  description: 'If not currently compiling, time in milliseconds to wait for a compile to start before returning. Default 1500.'
                }
              }
            }
          },
          {
            name: 'build_player',
            description: 'Triggers a Unity Player build asynchronously and returns immediately. Pair with wait_for_build_done to block until completion. Returns a snapshot of the build state.',
            inputSchema: {
              type: 'object',
              properties: {
                outputPath: {
                  type: 'string',
                  description: 'Absolute path to the player executable to write (e.g., D:/Builds/MyGame/MyGame.exe).'
                },
                target: {
                  type: 'string',
                  description: "Unity BuildTarget enum name. Common values: StandaloneWindows64, StandaloneOSX, StandaloneLinux64, Android, iOS, WebGL. Default StandaloneWindows64."
                },
                scenes: {
                  type: 'array',
                  items: { type: 'string' },
                  description: 'Optional explicit scene paths (e.g., Assets/Scenes/Main.unity). If omitted, uses enabled scenes from EditorBuildSettings.'
                },
                development: {
                  type: 'boolean',
                  default: false,
                  description: 'If true, builds with BuildOptions.Development.'
                },
                autoRunPlayer: {
                  type: 'boolean',
                  default: false,
                  description: 'If true, launches the built player after a successful build.'
                }
              },
              required: ['outputPath']
            }
          },
          {
            name: 'wait_for_build_done',
            description: 'Blocks until the most recent build (started via build_player) finishes, then returns the full report. Always use this instead of polling get_build_status.',
            inputSchema: {
              type: 'object',
              properties: {
                timeoutMs: {
                  type: 'number',
                  default: 600000,
                  description: 'Maximum time in milliseconds to wait. Default 600000 (10 min).'
                },
                pollMs: {
                  type: 'number',
                  default: 500,
                  description: 'Internal polling interval in milliseconds. Default 500.'
                }
              }
            }
          },
          {
            name: 'get_build_status',
            description: 'Returns the current build state (running flag, last report, errors). Use wait_for_build_done instead of polling this in a loop.',
            inputSchema: {
              type: 'object',
              properties: {}
            }
          },
          {
            name: 'get_compilation_errors',
            description: 'Retrieves the latest compilation errors and warnings from Unity\'s compilation pipeline.',
            inputSchema: {
              type: 'object',
              properties: {}
            }
          },
          {
            name: 'delete_gameobject',
            description: 'Deletes a GameObject from the Unity scene by path or instance ID. Supports undo.',
            inputSchema: {
              type: 'object',
              properties: {
                objectPath: {
                  type: 'string',
                  description: 'The path or name of the GameObject to delete (e.g. "Main Camera")'
                },
                instanceId: {
                  type: 'number',
                  description: 'The instance ID of the GameObject to delete'
                }
              }
            }
          },
          {
            name: 'remove_component',
            description: 'Removes a component from a GameObject in the Unity scene. Supports undo.',
            inputSchema: {
              type: 'object',
              properties: {
                objectPath: {
                  type: 'string',
                  description: 'The path or name of the GameObject (e.g. "Main Camera")'
                },
                instanceId: {
                  type: 'number',
                  description: 'The instance ID of the GameObject'
                },
                componentName: {
                  type: 'string',
                  description: 'The name of the component to remove (e.g. "Rigidbody", "BoxCollider")'
                }
              },
              required: ['componentName']
            }
          },
          {
            name: 'get_gameobject_info',
            description: 'Gets detailed information about a GameObject including Transform, components list, hierarchy info, and properties.',
            inputSchema: {
              type: 'object',
              properties: {
                objectPath: {
                  type: 'string',
                  description: 'The path or name of the GameObject (e.g. "Main Camera")'
                },
                instanceId: {
                  type: 'number',
                  description: 'The instance ID of the GameObject'
                }
              }
            }
          },
          {
            name: 'get_component_properties',
            description: 'Gets all serialized property values of a specific component on a GameObject.',
            inputSchema: {
              type: 'object',
              properties: {
                objectPath: {
                  type: 'string',
                  description: 'The path or name of the GameObject (e.g. "Main Camera")'
                },
                instanceId: {
                  type: 'number',
                  description: 'The instance ID of the GameObject'
                },
                componentName: {
                  type: 'string',
                  description: 'The name of the component to inspect (e.g. "Camera", "Rigidbody", "Transform")'
                }
              },
              required: ['componentName']
            }
          },
          {
            name: 'save_scene',
            description: 'Saves the currently active scene.',
            inputSchema: {
              type: 'object',
              properties: {}
            }
          },
          {
            name: 'open_scene',
            description: 'Opens a scene by its asset path.',
            inputSchema: {
              type: 'object',
              properties: {
                scenePath: {
                  type: 'string',
                  description: 'The asset path of the scene to open (e.g. "Assets/Scenes/MainScene.unity")'
                },
                additive: {
                  type: 'boolean',
                  description: 'Whether to open the scene additively (keeping current scene loaded). Default: false'
                }
              },
              required: ['scenePath']
            }
          },
          {
            name: 'find_assets',
            description: 'Searches the AssetDatabase for assets matching a filter. Uses Unity AssetDatabase.FindAssets syntax.',
            inputSchema: {
              type: 'object',
              properties: {
                filter: {
                  type: 'string',
                  description: 'Search filter string (e.g. "Player", "t:Material wood")'
                },
                type: {
                  type: 'string',
                  description: 'Asset type filter (e.g. "Material", "Prefab", "Scene", "Texture2D", "Script")'
                },
                searchInFolder: {
                  type: 'string',
                  description: 'Limit search to a specific folder (e.g. "Assets/Prefabs")'
                },
                limit: {
                  type: 'integer',
                  minimum: 1,
                  maximum: 200,
                  description: 'Maximum number of results to return (default: 50)'
                }
              }
            }
          },
          {
            name: 'create_material',
            description: 'Creates a new Material asset with the specified shader and color.',
            inputSchema: {
              type: 'object',
              properties: {
                name: {
                  type: 'string',
                  description: 'Name for the material (default: "New Material")'
                },
                shader: {
                  type: 'string',
                  description: 'Shader name (e.g. "Standard", "Universal Render Pipeline/Lit", "Unlit/Color"). Default: "Standard"'
                },
                savePath: {
                  type: 'string',
                  description: 'Asset path to save the material (e.g. "Assets/Materials/MyMaterial.mat"). Default: "Assets/{name}.mat"'
                },
                color: {
                  type: 'object',
                  description: 'Main color for the material (RGBA, 0-1 range)',
                  properties: {
                    r: { type: 'number', description: 'Red (0-1)' },
                    g: { type: 'number', description: 'Green (0-1)' },
                    b: { type: 'number', description: 'Blue (0-1)' },
                    a: { type: 'number', description: 'Alpha (0-1, default: 1)' }
                  }
                }
              }
            }
          },
          {
            name: 'get_material_properties',
            description: 'Gets all shader properties of a Material asset with their current values, types, and metadata (range limits, texture info, keywords).',
            inputSchema: {
              type: 'object',
              properties: {
                assetPath: {
                  type: 'string',
                  description: 'The asset path of the material (e.g. "Assets/Materials/MyMaterial.mat")'
                },
                guid: {
                  type: 'string',
                  description: 'The GUID of the material asset (alternative to assetPath)'
                }
              }
            }
          },
          {
            name: 'set_material_property',
            description: 'Sets a shader property on a Material asset. Supports Float, Int, Color, Vector, Texture, Range, and Keyword types. Also supports setting renderQueue. Supports undo.',
            inputSchema: {
              type: 'object',
              properties: {
                assetPath: {
                  type: 'string',
                  description: 'The asset path of the material (e.g. "Assets/Materials/MyMaterial.mat")'
                },
                guid: {
                  type: 'string',
                  description: 'The GUID of the material asset (alternative to assetPath)'
                },
                propertyName: {
                  type: 'string',
                  description: 'The shader property name (e.g. "_Color", "_MainTex", "_Metallic", "_Glossiness", "renderQueue")'
                },
                propertyType: {
                  type: 'string',
                  enum: ['Float', 'Int', 'Color', 'Vector', 'Texture', 'Range', 'Keyword'],
                  description: 'The type of the property. If omitted, auto-detected from shader. Use "Keyword" to toggle shader keywords.'
                },
                value: {
                  description: 'The value to set. Format depends on propertyType: Float/Range=number, Int=integer, Color={r,g,b,a}, Vector={x,y,z,w}, Texture={path,guid,offset?,scale?} or "path" string, Keyword=boolean (enable/disable).'
                },
                keyword: {
                  type: 'string',
                  description: 'The shader keyword name (only used when propertyType is "Keyword", e.g. "_EMISSION", "_NORMALMAP")'
                }
              }
            }
          },
          {
            name: 'invoke_method',
            description: 'Invokes a static or instance method via reflection (generic editor-automation primitive). Use it to call project-side editor routines such as build/migration helpers (e.g. a class that uses PrefabUtility.LoadPrefabContents/SaveAsPrefabAsset), VcamRigBuilder.Generate, etc. Method arguments are JSON values converted to the parameter types, including UnityEngine.Object references resolved by instanceId / assetPath / guid / objectPath. WARNING: this can run arbitrary editor code; intended for trusted local development only.',
            inputSchema: {
              type: 'object',
              properties: {
                typeName: {
                  type: 'string',
                  description: 'Fully-qualified or simple type name declaring the method (e.g. "vortex.VcamRigBuilder" or "VcamRigBuilder")'
                },
                methodName: {
                  type: 'string',
                  description: 'The method name to invoke. Overloads are disambiguated by the number of args.'
                },
                args: {
                  type: 'array',
                  description: 'Ordered method arguments as JSON values. Primitives/enums/Vector3/etc. convert directly; objects with {instanceId|assetPath|guid|objectPath[,componentName]} resolve to UnityEngine.Object references.',
                  items: {}
                },
                target: {
                  description: 'For instance methods: the object the method is called on, given as instanceId (number) or {instanceId|assetPath|guid|objectPath[,componentName]}. Omit for static methods.'
                }
              },
              required: ['typeName', 'methodName']
            }
          },
          {
            name: 'create_asset',
            description: 'Creates a ScriptableObject asset (ScriptableObject.CreateInstance + AssetDatabase.CreateAsset) at the given project path, optionally setting initial field values. Returns the created asset path and GUID.',
            inputSchema: {
              type: 'object',
              properties: {
                typeName: {
                  type: 'string',
                  description: 'Fully-qualified or simple ScriptableObject type name (e.g. "vortex.CameraRigProfile")'
                },
                path: {
                  type: 'string',
                  description: 'Destination asset path, must start with "Assets/" or "Packages/" (e.g. "Assets/Profiles/Cam01.asset")'
                },
                data: {
                  type: 'object',
                  description: 'Optional initial field values. Same value semantics as update_component (UnityEngine.Object refs, nested lists/POCOs supported).'
                },
                unique: {
                  type: 'boolean',
                  description: 'If true (default), GenerateUniqueAssetPath avoids overwriting an existing asset at path.'
                }
              },
              required: ['typeName', 'path']
            }
          },
          {
            name: 'set_object_properties',
            description: 'Sets fields/properties on any UnityEngine.Object — a component OR a standalone asset (e.g. a ScriptableObject). Generalizes update_component to assets. Resolves object references / nested lists / POCOs the same way update_component does, records Undo, marks dirty, and saves the asset if it is on disk.',
            inputSchema: {
              type: 'object',
              properties: {
                target: {
                  description: 'The object to modify, given as instanceId (number) or {instanceId|assetPath|guid|objectPath[,componentName]}. May also be supplied via the top-level instanceId/assetPath/guid/objectPath fields.'
                },
                instanceId: { type: 'number', description: 'Target by instance ID (alternative to target).' },
                assetPath: { type: 'string', description: 'Target an asset by path (alternative to target).' },
                guid: { type: 'string', description: 'Target an asset by GUID (alternative to target).' },
                objectPath: { type: 'string', description: 'Target a scene GameObject by path (alternative to target).' },
                componentName: { type: 'string', description: 'When objectPath resolves a GameObject, pick this component as the target.' },
                typeName: { type: 'string', description: 'Optional type hint used when loading an asset by path/guid.' },
                data: {
                  type: 'object',
                  description: 'Object of field/property names to values. Same value semantics as update_component.'
                }
              },
              required: ['data']
            }
          }
        ]
      };
    });

    // List available resources
    this.server.setRequestHandler(ListResourcesRequestSchema, async () => {
      return {
        resources: [
          {
            uri: 'unity://scenes_hierarchy',
            mimeType: 'application/json',
            name: 'Scene Hierarchy',
            description: 'Current Unity scene hierarchy with all GameObjects'
          }
        ]
      };
    });

    // Read resource
    this.server.setRequestHandler(ReadResourceRequestSchema, async (request) => {
      const { uri } = request.params;

      if (uri === 'unity://scenes_hierarchy') {
        try {
          const response = await fetch(`${this.unityUrl}/mcp/resources/scenes_hierarchy`);
          const data = await response.json();
          
          return {
            contents: [
              {
                uri: uri,
                mimeType: 'application/json',
                text: JSON.stringify(data, null, 2)
              }
            ]
          };
        } catch (error) {
          throw new Error(`Failed to fetch scene hierarchy: ${error.message}`);
        }
      }

      throw new Error(`Unknown resource: ${uri}`);
    });

    // Handle tool calls
    this.server.setRequestHandler(CallToolRequestSchema, async (request) => {
      const { name, arguments: args } = request.params;

      try {
        // Map tool names to Unity endpoints
        const endpoint = `/mcp/tools/${name}`;
        
        console.error(`[MCP Unity] Calling tool: ${name} with args:`, JSON.stringify(args));
        
        const response = await fetch(`${this.unityUrl}${endpoint}`, {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
          },
          body: JSON.stringify(args || {}),
        });

        if (!response.ok) {
          throw new Error(`HTTP ${response.status}: ${response.statusText}`);
        }

        const data = await response.json();
        console.error(`[MCP Unity] Tool response for: ${name}`);

        return {
          content: [
            {
              type: 'text',
              text: JSON.stringify(data, null, 2)
            }
          ]
        };
      } catch (error) {
        console.error(`[MCP Unity] Tool call failed: ${error.message}`);
        return {
          content: [
            {
              type: 'text',
              text: `Error: ${error.message}`
            }
          ],
          isError: true
        };
      }
    });
  }

  async run() {
    const transport = new StdioServerTransport();
    await this.server.connect(transport);
    console.error('[MCP Unity] Server started, Unity URL:', this.unityUrl);
  }
}

// Start the server
const server = new ClaudeCodeMCPUnityServer();
server.run().catch(console.error);