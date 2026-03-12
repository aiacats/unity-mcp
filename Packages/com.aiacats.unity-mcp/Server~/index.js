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
            description: 'Adds packages into the Unity Package Manager',
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
            name: 'play_mode_control',
            description: 'Controls Unity Editor play mode (play, stop, pause) or gets current status.',
            inputSchema: {
              type: 'object',
              properties: {
                action: {
                  type: 'string',
                  enum: ['play', 'stop', 'pause', 'status'],
                  description: 'The action to perform: play, stop, pause (toggle), or status'
                }
              },
              required: ['action']
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
            name: 'screenshot',
            description: 'Captures a screenshot from the Game view.',
            inputSchema: {
              type: 'object',
              properties: {
                savePath: {
                  type: 'string',
                  description: 'Path to save the screenshot (e.g. "Assets/Screenshots/shot.png"). Default: auto-generated with timestamp'
                },
                superSize: {
                  type: 'integer',
                  minimum: 1,
                  maximum: 8,
                  description: 'Resolution multiplier (1 = normal, 2 = double, etc.). Default: 1'
                }
              }
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
        console.error(`[MCP Unity] Tool response:`, JSON.stringify(data));

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