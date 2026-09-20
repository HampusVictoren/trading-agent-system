"""The agent service.

Configuration used to be loaded here with load_dotenv, because memory.py read environment
variables at import time and therefore depended on import order. Both are gone: settings
are read on demand in app.settings, which reads the .env file itself.
"""
